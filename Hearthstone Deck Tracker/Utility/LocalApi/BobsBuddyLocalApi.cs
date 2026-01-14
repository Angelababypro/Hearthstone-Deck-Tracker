using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hearthstone_Deck_Tracker.BobsBuddy;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Utility.Assets;
using Hearthstone_Deck_Tracker.Utility.Battlegrounds;
using Hearthstone_Deck_Tracker.Utility.Logging;
using HearthDb.Enums;
using Newtonsoft.Json;

namespace Hearthstone_Deck_Tracker.Utility.LocalApi
{
	internal static class BobsBuddyLocalApi
	{
		private const int DefaultPort = 32123;
		private static readonly object Sync = new();
		private static readonly SemaphoreSlim SimulationGate = new(1, 1);
		private static HttpListener? _listener;
		private static CancellationTokenSource? _cts;
		private static Task? _listenerTask;
		private static readonly object CardsSync = new();
		private static string? _cachedCardsJson;
		private static bool _cardHooksRegistered;

		internal static void Start()
		{
			lock(Sync)
			{
				if(_listener != null)
					return;

				_cts = new CancellationTokenSource();
				_listener = new HttpListener();
				var prefix = $"http://127.0.0.1:{DefaultPort}/";
				_listener.Prefixes.Add(prefix);
				try
				{
					_listener.Start();
				}
				catch(Exception e)
				{
					Log.Error(e);
					_listener = null;
					_cts.Dispose();
					_cts = null;
					return;
				}

				_listenerTask = Task.Run(() => ListenLoopAsync(_listener, _cts.Token));
				Log.Info($"BobsBuddyLocalApi listening on {prefix}");

				if(!_cardHooksRegistered)
				{
					CardDefsManager.CardsChanged += ClearCardsCache;
					CardDefsManager.InitialDefsLoaded += ClearCardsCache;
					_cardHooksRegistered = true;
				}
			}
		}

		internal static void Stop()
		{
			lock(Sync)
			{
				if(_listener == null)
					return;
				_cts?.Cancel();
				_listener.Close();
				_listener = null;
				_cts?.Dispose();
				_cts = null;
			}
		}

		private static async Task ListenLoopAsync(HttpListener listener, CancellationToken ct)
		{
			while(!ct.IsCancellationRequested)
			{
				HttpListenerContext context;
				try
				{
					context = await listener.GetContextAsync().ConfigureAwait(false);
				}
				catch(HttpListenerException)
				{
					break;
				}
				catch(ObjectDisposedException)
				{
					break;
				}

				_ = HandleContextAsync(context, ct);
			}
		}

		private static async Task HandleContextAsync(HttpListenerContext context, CancellationToken ct)
		{
			var request = context.Request;
			var response = context.Response;

			try
			{
				var path = request.Url?.AbsolutePath?.TrimEnd('/') ?? string.Empty;
				if(path.Length == 0)
					path = "/";

				if(path.Equals("/health", StringComparison.OrdinalIgnoreCase))
				{
					await WriteJsonAsync(response, new { status = "ok" }).ConfigureAwait(false);
					return;
				}

				if(path.Equals("/builder", StringComparison.OrdinalIgnoreCase) || path.Equals("/", StringComparison.OrdinalIgnoreCase))
				{
					if(!request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
					{
						await WriteTextAsync(response, "Method Not Allowed", "text/plain", 405).ConfigureAwait(false);
						return;
					}

					await WriteTextAsync(response, BuilderHtml, "text/html").ConfigureAwait(false);
					return;
				}

				if(path.Equals("/cards", StringComparison.OrdinalIgnoreCase))
				{
					if(!request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
					{
						await WriteJsonAsync(response, new { error = "method_not_allowed" }, 405).ConfigureAwait(false);
						return;
					}

					var json = GetCardsJson();
					await WriteTextAsync(response, json, "application/json").ConfigureAwait(false);
					return;
				}

				if(path.Equals("/simulate/from-current", StringComparison.OrdinalIgnoreCase))
				{
					if(!request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
					{
						await WriteJsonAsync(response, new { error = "method_not_allowed" }, 405).ConfigureAwait(false);
						return;
					}

					var options = ParseSimOptions(request);
					await SimulationGate.WaitAsync(ct).ConfigureAwait(false);
					try
					{
						if(!Core.Game.IsBattlegroundsMatch)
						{
							await WriteJsonAsync(response, new { error = "not_in_battlegrounds" }, 400).ConfigureAwait(false);
							return;
						}

						var result = await BobsBuddyInvoker.SimulateFromCurrentAsync(options, ct).ConfigureAwait(false);
						if(result == null)
						{
							await WriteJsonAsync(response, new { error = "simulation_failed" }, 500).ConfigureAwait(false);
							return;
						}

						await WriteJsonAsync(response, new
						{
							win = result.Win,
							tie = result.Tie,
							lose = result.Lose,
							simulations = result.Simulations
						}).ConfigureAwait(false);
						return;
					}
					finally
					{
						SimulationGate.Release();
					}
				}

				if(path.Equals("/simulate", StringComparison.OrdinalIgnoreCase))
				{
					if(!request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
					{
						await WriteJsonAsync(response, new { error = "method_not_allowed" }, 405).ConfigureAwait(false);
						return;
					}

					var body = await ReadBodyAsync(request).ConfigureAwait(false);
					if(string.IsNullOrWhiteSpace(body))
					{
						await WriteJsonAsync(response, new { error = "empty_body" }, 400).ConfigureAwait(false);
						return;
					}

					var snapshot = JsonConvert.DeserializeObject<CustomBattleSnapshot>(body);
					if(snapshot == null)
					{
						await WriteJsonAsync(response, new { error = "invalid_body" }, 400).ConfigureAwait(false);
						return;
					}

					var options = ParseSimOptions(request);
					await SimulationGate.WaitAsync(ct).ConfigureAwait(false);
					try
					{
						var result = await BobsBuddyInvoker.SimulateCustomAsync(snapshot, options, ct).ConfigureAwait(false);
						if(result == null)
						{
							await WriteJsonAsync(response, new { error = "simulation_failed" }, 500).ConfigureAwait(false);
							return;
						}

						await WriteJsonAsync(response, new
						{
							win = result.Win,
							tie = result.Tie,
							lose = result.Lose,
							simulations = result.Simulations
						}).ConfigureAwait(false);
						return;
					}
					finally
					{
						SimulationGate.Release();
					}
				}

				await WriteJsonAsync(response, new { error = "not_found" }, 404).ConfigureAwait(false);
			}
			catch(Exception e)
			{
				Log.Error(e);
				await WriteJsonAsync(response, new { error = "server_error" }, 500).ConfigureAwait(false);
			}
			finally
			{
				response.OutputStream.Close();
			}
		}

		private static SimOptions ParseSimOptions(HttpListenerRequest request)
		{
			var options = new SimOptions();

			if(int.TryParse(request.QueryString["iterations"], out var iterations) && iterations > 0)
				options.Iterations = iterations;

			if(int.TryParse(request.QueryString["timeoutMs"], out var timeoutMs) && timeoutMs > 0)
				options.TimeoutMs = timeoutMs;

			if(int.TryParse(request.QueryString["threads"], out var threads) && threads > 0)
				options.ThreadCount = threads;

			return options;
		}

		private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
		{
			if(!request.HasEntityBody)
				return string.Empty;

			using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
			return await reader.ReadToEndAsync().ConfigureAwait(false);
		}

		private static async Task WriteJsonAsync(HttpListenerResponse response, object payload, int statusCode = 200)
		{
			var json = JsonConvert.SerializeObject(payload);
			var buffer = Encoding.UTF8.GetBytes(json);
			response.StatusCode = statusCode;
			response.ContentType = "application/json";
			response.ContentLength64 = buffer.Length;
			await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
		}

		private static async Task WriteTextAsync(HttpListenerResponse response, string text, string contentType, int statusCode = 200)
		{
			var buffer = Encoding.UTF8.GetBytes(text);
			response.StatusCode = statusCode;
			response.ContentType = contentType;
			response.ContentLength64 = buffer.Length;
			await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
		}

		private static string GetCardsJson()
		{
			lock(CardsSync)
			{
				if(_cachedCardsJson != null)
					return _cachedCardsJson;

				if(!CardDefsManager.HasLoadedInitialBaseDefs)
				{
					_cachedCardsJson = JsonConvert.SerializeObject(new
					{
						ready = false,
						cards = Array.Empty<object>()
					});
					return _cachedCardsJson;
				}

				var races = BattlegroundsDbSingleton.Instance.Races;
				var raceList = races.Count > 0
					? races
					: Enum.GetValues(typeof(Race)).Cast<Race>().Where(r => r != Race.INVALID && r != Race.ALL).ToHashSet();

				var poolCards = BattlegroundsDbSingleton.Instance
					.GetCardsByRaces(raceList, Core.Game.IsBattlegroundsDuosMatch)
					.Where(c => c.TypeEnum == CardType.MINION)
					.ToList();

				var tokenCards = HearthDb.Cards.All.Values
					.Where(IsBattlegroundsMinion)
					.Select(c => new Card(c, true))
					.ToList();

				var cards = poolCards
					.Concat(tokenCards)
					.GroupBy(c => c.Id)
					.Select(g => g.First())
					.Select(c => new
					{
						id = c.Id,
						name = c.LocalizedName ?? c.Name ?? c.Id
					})
					.OrderBy(c => c.name)
					.ToList();

				_cachedCardsJson = JsonConvert.SerializeObject(new
				{
					ready = true,
					cards
				});
				return _cachedCardsJson;
			}
		}

		private static bool IsBattlegroundsMinion(HearthDb.Card card)
		{
			if(card.Type != CardType.MINION)
				return false;

			var id = card.Id ?? string.Empty;
			if(id.StartsWith("BGS_", StringComparison.OrdinalIgnoreCase) ||
			   id.StartsWith("BG_", StringComparison.OrdinalIgnoreCase) ||
			   id.StartsWith("TB_Bacon", StringComparison.OrdinalIgnoreCase))
				return true;

			if(card.Set == CardSet.BATTLEGROUNDS)
				return true;

			var entity = card.Entity;
			if(entity == null)
				return false;

			return entity.GetTag(GameTag.TECH_LEVEL) > 0
				|| entity.GetTag(GameTag.IS_BACON_POOL_MINION) > 0
				|| entity.GetTag(GameTag.BACON_BUDDY) > 0;
		}

		private static void ClearCardsCache()
		{
			lock(CardsSync)
			{
				_cachedCardsJson = null;
			}
		}

		private const string BuilderHtml = @"<!doctype html>
<html lang=""en"">
<head>
  <meta charset=""utf-8"" />
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
  <title>HDT BobsBuddy Builder</title>
  <style>
    :root { --bg: #121417; --panel: #1c2127; --text: #e8edf2; --muted: #9aa7b5; --accent: #f1c40f; --border: #2c333b; }
    * { box-sizing: border-box; }
    body { margin: 0; font-family: ""Segoe UI"", Tahoma, sans-serif; background: var(--bg); color: var(--text); }
    header { padding: 16px 20px; border-bottom: 1px solid var(--border); display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
    header h1 { font-size: 18px; margin: 0; }
    main { padding: 16px 20px; display: grid; gap: 16px; }
    .grid { display: grid; gap: 16px; grid-template-columns: 1fr; }
    @media (min-width: 900px) { .grid { grid-template-columns: 1fr 1fr; } }
    .panel { background: var(--panel); border: 1px solid var(--border); border-radius: 8px; padding: 12px; }
    .panel h2 { font-size: 16px; margin: 0 0 8px; }
    .row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
    label { font-size: 12px; color: var(--muted); display: block; }
    input, select, textarea, button { background: #0e1114; color: var(--text); border: 1px solid var(--border); border-radius: 6px; padding: 6px 8px; }
    input[type=""number""] { width: 70px; }
    .minion { display: grid; grid-template-columns: 1.5fr 70px 70px 70px auto auto; gap: 6px; align-items: center; margin-bottom: 6px; }
    .minion input[type=""text""] { width: 100%; }
    .tags { display: flex; gap: 6px; flex-wrap: wrap; }
    .tag { display: inline-flex; gap: 4px; align-items: center; font-size: 12px; color: var(--muted); }
    .actions { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
    .btn { background: #1f2a33; border-color: #2d3943; cursor: pointer; }
    .btn.accent { background: #2a2613; border-color: #4a3d12; color: var(--accent); }
    .btn.danger { background: #2a1414; border-color: #4a1212; color: #ff6b6b; }
    textarea { width: 100%; min-height: 140px; }
    .result { font-family: Consolas, monospace; font-size: 13px; }
    .search { display: grid; gap: 6px; }
    .results { max-height: 180px; overflow: auto; border: 1px solid var(--border); border-radius: 6px; }
    .result-item { display: block; width: 100%; text-align: left; padding: 6px 8px; border: 0; border-bottom: 1px solid var(--border); background: #0f1216; color: var(--text); cursor: pointer; }
    .result-item:last-child { border-bottom: 0; }
    .muted { color: var(--muted); font-size: 12px; }
  </style>
</head>
<body>
  <header>
    <h1>HDT Bob's Buddy Builder</h1>
    <span class=""muted"">127.0.0.1:32123</span>
    <span id=""cardsStatus"" class=""muted"">loading cards...</span>
  </header>
  <main>
    <div class=""panel search"">
      <label>Card Search (type name, click to fill the last focused card field)</label>
      <input id=""cardSearch"" type=""text"" placeholder=""Search name or id"" />
      <div id=""searchResults"" class=""results""></div>
    </div>

    <div class=""panel"">
      <div class=""row"">
        <div>
          <label>Iterations</label>
          <input id=""iterations"" type=""number"" value=""10000"" />
        </div>
        <div>
          <label>Timeout (ms)</label>
          <input id=""timeoutMs"" type=""number"" value=""1500"" />
        </div>
        <div>
          <label>Threads</label>
          <input id=""threads"" type=""number"" value="""" placeholder=""auto"" />
        </div>
        <div>
          <label>Anomaly (cardId)</label>
          <input id=""anomaly"" type=""text"" placeholder=""optional"" />
        </div>
      </div>
    </div>

    <div class=""grid"">
      <div class=""panel"">
        <h2>Player</h2>
        <div class=""row"">
          <div><label>Health</label><input id=""pHealth"" type=""number"" value=""40"" /></div>
          <div><label>Armor</label><input id=""pArmor"" type=""number"" value=""0"" /></div>
          <div><label>Tier</label><input id=""pTier"" type=""number"" value=""0"" /></div>
        </div>
        <div id=""playerMinions""></div>
        <div class=""actions"">
          <button class=""btn"" onclick=""addMinion('player')"">+ Add Minion</button>
          <button class=""btn danger"" onclick=""clearMinions('player')"">Clear</button>
        </div>
      </div>

      <div class=""panel"">
        <h2>Opponent</h2>
        <div class=""row"">
          <div><label>Health</label><input id=""oHealth"" type=""number"" value=""40"" /></div>
          <div><label>Armor</label><input id=""oArmor"" type=""number"" value=""0"" /></div>
          <div><label>Tier</label><input id=""oTier"" type=""number"" value=""0"" /></div>
        </div>
        <div id=""opponentMinions""></div>
        <div class=""actions"">
          <button class=""btn"" onclick=""addMinion('opponent')"">+ Add Minion</button>
          <button class=""btn danger"" onclick=""clearMinions('opponent')"">Clear</button>
        </div>
      </div>
    </div>

    <div class=""panel"">
      <div class=""actions"">
        <button class=""btn accent"" onclick=""simulate()"">Simulate</button>
        <button class=""btn"" onclick=""exportJson()"">Export JSON</button>
        <button class=""btn"" onclick=""importJson()"">Import JSON</button>
      </div>
      <label>JSON</label>
      <textarea id=""json""></textarea>
      <label>Result</label>
      <div class=""result"" id=""result"">-</div>
    </div>
  </main>

  <template id=""minionTemplate"">
    <div class=""minion"">
      <input type=""text"" class=""cardId"" placeholder=""cardId (e.g. BGS_004)"" />
      <input type=""number"" class=""atk"" placeholder=""atk"" />
      <input type=""number"" class=""hp"" placeholder=""hp"" />
      <input type=""number"" class=""tier"" placeholder=""tier"" />
      <label class=""tag""><input type=""checkbox"" value=""taunt"">taunt</label>
      <label class=""tag""><input type=""checkbox"" value=""divineShield"">divine</label>
      <label class=""tag""><input type=""checkbox"" value=""reborn"">reborn</label>
      <label class=""tag""><input type=""checkbox"" value=""poisonous"">poison</label>
      <label class=""tag""><input type=""checkbox"" value=""venomous"">venom</label>
      <label class=""tag""><input type=""checkbox"" value=""windfury"">windfury</label>
      <label class=""tag""><input type=""checkbox"" value=""megaWindfury"">mega</label>
      <label class=""tag""><input type=""checkbox"" value=""stealth"">stealth</label>
      <label class=""tag""><input type=""checkbox"" value=""golden"">golden</label>
      <button class=""btn danger"" onclick=""removeMinion(this)"">Remove</button>
    </div>
  </template>

  <script>
    let cardList = [];
    let cardByName = new Map();
    let cardById = new Map();
    let activeCardInput = null;

    document.addEventListener('focusin', (e) => {
      if(e.target && e.target.classList && e.target.classList.contains('cardId')) {
        activeCardInput = e.target;
      }
    });

    async function loadCards() {
      const status = document.getElementById('cardsStatus');
      try {
        const resp = await fetch('/cards');
        const data = await resp.json();
        if(!data.ready) {
          status.textContent = 'cards not ready';
          return;
        }
        cardList = data.cards || [];
        cardById.clear();
        cardByName.clear();
        cardList.forEach(c => {
          cardById.set(c.id.toLowerCase(), c);
          const key = (c.name || '').toLowerCase();
          if(key && !cardByName.has(key)) {
            cardByName.set(key, c);
          }
        });
        status.textContent = `cards: ${cardList.length}`;
      } catch (e) {
        status.textContent = 'cards failed to load';
      }
    }

    function renderSearchResults(query) {
      const results = document.getElementById('searchResults');
      results.innerHTML = '';
      const q = query.trim().toLowerCase();
      if(!q) return;
      const matches = cardList.filter(c =>
        (c.name && c.name.toLowerCase().includes(q)) || c.id.toLowerCase().includes(q)
      ).slice(0, 20);
      matches.forEach(c => {
        const btn = document.createElement('button');
        btn.className = 'result-item';
        btn.textContent = `${c.name} (${c.id})`;
        btn.onclick = () => {
          if(activeCardInput) {
            activeCardInput.value = c.id;
            activeCardInput.focus();
          }
        };
        results.appendChild(btn);
      });
    }

    document.getElementById('cardSearch').addEventListener('input', (e) => {
      renderSearchResults(e.target.value);
    });

    function addMinion(side) {
      const tmpl = document.getElementById('minionTemplate');
      const clone = tmpl.content.cloneNode(true);
      const container = side === 'player' ? document.getElementById('playerMinions') : document.getElementById('opponentMinions');
      container.appendChild(clone);
    }

    function clearMinions(side) {
      const container = side === 'player' ? document.getElementById('playerMinions') : document.getElementById('opponentMinions');
      container.innerHTML = '';
    }

    function removeMinion(btn) {
      const row = btn.closest('.minion');
      if(row) row.remove();
    }

    function readMinions(container) {
      const rows = Array.from(container.querySelectorAll('.minion'));
      return rows.map(row => {
        const raw = row.querySelector('.cardId').value.trim();
        if(!raw) return null;
        const cardId = resolveCardId(raw);
        if(!cardId) return null;
        const atk = row.querySelector('.atk').value;
        const hp = row.querySelector('.hp').value;
        const tier = row.querySelector('.tier').value;
        const tags = Array.from(row.querySelectorAll('input[type=checkbox]:checked')).map(x => x.value);
        const golden = tags.includes('golden');
        const filteredTags = tags.filter(t => t !== 'golden');
        const minion = { cardId: cardId };
        if(atk) minion.atk = parseInt(atk, 10);
        if(hp) minion.hp = parseInt(hp, 10);
        if(tier) minion.tier = parseInt(tier, 10);
        if(filteredTags.length) minion.tags = filteredTags;
        if(golden) minion.golden = true;
        return minion;
      }).filter(x => x !== null);
    }

    function resolveCardId(value) {
      const v = value.trim();
      if(!v) return null;
      const lower = v.toLowerCase();
      if(cardById.has(lower)) return cardById.get(lower).id;
      if(cardByName.has(lower)) return cardByName.get(lower).id;
      const match = v.match(/\(([^)]+)\)\s*$/);
      if(match && match[1]) return match[1];
      return v;
    }

    function buildSnapshot() {
      const p = {
        health: parseInt(document.getElementById('pHealth').value, 10) || 40,
        armor: parseInt(document.getElementById('pArmor').value, 10) || 0,
        tier: parseInt(document.getElementById('pTier').value, 10) || 0,
        minions: readMinions(document.getElementById('playerMinions'))
      };
      const o = {
        health: parseInt(document.getElementById('oHealth').value, 10) || 40,
        armor: parseInt(document.getElementById('oArmor').value, 10) || 0,
        tier: parseInt(document.getElementById('oTier').value, 10) || 0,
        minions: readMinions(document.getElementById('opponentMinions'))
      };
      const snapshot = { player: p, opponent: o };
      const anomaly = document.getElementById('anomaly').value.trim();
      if(anomaly) snapshot.anomalyCardId = anomaly;
      return snapshot;
    }

    function exportJson() {
      const snapshot = buildSnapshot();
      document.getElementById('json').value = JSON.stringify(snapshot, null, 2);
    }

    function importJson() {
      const text = document.getElementById('json').value.trim();
      if(!text) return;
      let data;
      try { data = JSON.parse(text); } catch (e) { alert('Invalid JSON'); return; }
      clearMinions('player');
      clearMinions('opponent');
      document.getElementById('pHealth').value = data.player?.health ?? 40;
      document.getElementById('pArmor').value = data.player?.armor ?? 0;
      document.getElementById('pTier').value = data.player?.tier ?? 0;
      document.getElementById('oHealth').value = data.opponent?.health ?? 40;
      document.getElementById('oArmor').value = data.opponent?.armor ?? 0;
      document.getElementById('oTier').value = data.opponent?.tier ?? 0;
      document.getElementById('anomaly').value = data.anomalyCardId ?? '';

      const pMinions = data.player?.minions || [];
      pMinions.forEach(m => addMinionWithData('player', m));
      const oMinions = data.opponent?.minions || [];
      oMinions.forEach(m => addMinionWithData('opponent', m));
    }

    function addMinionWithData(side, data) {
      addMinion(side);
      const container = side === 'player' ? document.getElementById('playerMinions') : document.getElementById('opponentMinions');
      const row = container.lastElementChild;
      if(!row) return;
      row.querySelector('.cardId').value = data.cardId || '';
      if(data.atk !== undefined) row.querySelector('.atk').value = data.atk;
      if(data.hp !== undefined) row.querySelector('.hp').value = data.hp;
      if(data.tier !== undefined) row.querySelector('.tier').value = data.tier;
      const tags = new Set(data.tags || []);
      if(data.golden) tags.add('golden');
      row.querySelectorAll('input[type=checkbox]').forEach(cb => {
        cb.checked = tags.has(cb.value);
      });
    }

    async function simulate() {
      const snapshot = buildSnapshot();
      const iterations = document.getElementById('iterations').value.trim();
      const timeoutMs = document.getElementById('timeoutMs').value.trim();
      const threads = document.getElementById('threads').value.trim();
      const params = new URLSearchParams();
      if(iterations) params.set('iterations', iterations);
      if(timeoutMs) params.set('timeoutMs', timeoutMs);
      if(threads) params.set('threads', threads);
      const url = '/simulate' + (params.toString() ? '?' + params.toString() : '');
      const resultEl = document.getElementById('result');
      resultEl.textContent = 'running...';
      try {
        const resp = await fetch(url, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(snapshot)
        });
        const data = await resp.json();
        if(!resp.ok) {
          resultEl.textContent = JSON.stringify(data);
          return;
        }
        resultEl.textContent = `win=${(data.win*100).toFixed(1)}% tie=${(data.tie*100).toFixed(1)}% lose=${(data.lose*100).toFixed(1)}% sims=${data.simulations}`;
      } catch (e) {
        resultEl.textContent = 'error';
      }
    }

    addMinion('player');
    addMinion('opponent');
    loadCards();
  </script>
</body>
</html>";
	}
}
