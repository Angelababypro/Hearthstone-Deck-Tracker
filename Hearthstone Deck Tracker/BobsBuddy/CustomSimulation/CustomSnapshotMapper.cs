using System;
using System.Collections.Generic;
using System.Linq;
using BobsBuddy.Factory;
using BobsBuddy.Simulation;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;

namespace Hearthstone_Deck_Tracker.BobsBuddy
{
	internal static class CustomSnapshotMapper
	{
		internal static Input? ToInput(CustomBattleSnapshot snapshot)
		{
			if(snapshot.Player == null || snapshot.Opponent == null)
				return null;

			var simulator = new Simulator();
			var input = new Input();

			input.availableRaces = ResolveAvailableRaces(snapshot).ToList();
			if(snapshot.DamageCap.HasValue)
				input.DamageCap = snapshot.DamageCap.Value;

			var anomalyId = snapshot.AnomalyCardId;
			if(!string.IsNullOrWhiteSpace(anomalyId))
				input.Anomaly = simulator.AnomalyFactory.Create(anomalyId!);

			ApplySideSnapshot(simulator, input.Player, snapshot.Player, true);
			ApplySideSnapshot(simulator, input.Opponent, snapshot.Opponent, false);

			return input;
		}

		private static IEnumerable<Race> ResolveAvailableRaces(CustomBattleSnapshot snapshot)
		{
			if(snapshot.AvailableRaces != null && snapshot.AvailableRaces.Count > 0)
			{
				foreach(var raceName in snapshot.AvailableRaces)
				{
					if(Enum.TryParse<Race>(raceName, true, out var race) && race != Race.INVALID)
						yield return race;
				}
				yield break;
			}

			var available = BattlegroundsUtils.GetAvailableRaces();
			if(available != null && available.Count > 0)
			{
				foreach(var race in available)
					yield return race;
				yield break;
			}

			foreach(var race in Enum.GetValues(typeof(Race)).Cast<Race>())
			{
				if(race != Race.INVALID)
					yield return race;
			}
		}

		private static void ApplySideSnapshot(Simulator simulator, global::BobsBuddy.Simulation.Player target, CustomSideSnapshot side, bool isPlayer)
		{
			var health = side.Health ?? 40;
			var armor = side.Armor ?? 0;
			target.Health = health + armor;
			target.DamageTaken = side.DamageTaken ?? 0;
			target.Tier = side.Tier ?? 0;

			var minions = side.Minions ?? Array.Empty<CustomMinion>();
			var gameId = 1;
			foreach(var minion in minions)
			{
				var mapped = CreateMinion(simulator, minion, isPlayer, gameId++);
				if(mapped != null)
					target.Side.Add(mapped);
			}
		}

		private static global::BobsBuddy.Minion? CreateMinion(Simulator simulator, CustomMinion minion, bool isPlayer, int gameId)
		{
			if(string.IsNullOrWhiteSpace(minion.CardId))
				return null;

			var mapped = simulator.MinionFactory.CreateFromCardId(minion.CardId, isPlayer);
			mapped.golden = minion.Golden ?? false;

			if(minion.Atk.HasValue)
			{
				mapped.baseAttack = minion.Atk.Value;
				mapped.maxAttack = minion.Atk.Value;
			}

			if(minion.Hp.HasValue)
			{
				mapped.baseHealth = minion.Hp.Value;
				mapped.maxHealth = minion.Hp.Value;
			}

			if(minion.Tier.HasValue)
				mapped.tier = minion.Tier.Value;

			var tags = new HashSet<string>(minion.Tags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
			mapped.taunt = tags.Contains("taunt");
			mapped.div = tags.Contains("divineshield") || tags.Contains("divineShield") ? 1 : 0;
			mapped.reborn = tags.Contains("reborn");
			mapped.poisonous = tags.Contains("poisonous");
			mapped.venomous = tags.Contains("venomous");
			mapped.windfury = tags.Contains("windfury");
			mapped.megaWindfury = tags.Contains("megawindfury") || tags.Contains("megaWindfury");
			mapped.stealth = tags.Contains("stealth");
			mapped.cleave = MinionFactory.cardIDsWithCleave.Contains(mapped.CardID);

			if(minion.ScriptDataNum1.HasValue)
				mapped.ScriptDataNum1 = minion.ScriptDataNum1.Value;
			if(minion.ScriptDataNum2.HasValue)
				mapped.ScriptDataNum2 = minion.ScriptDataNum2.Value;
			if(minion.ScriptDataNum3.HasValue)
				mapped.ScriptDataNum3 = minion.ScriptDataNum3.Value;

			if(mapped.golden && MinionFactory.cardIdsWithoutPremiumImplementations.Contains(mapped.CardID))
			{
				mapped.vanillaAttack *= 2;
				mapped.vanillaHealth *= 2;
			}

			mapped.game_id = gameId;
			return mapped;
		}
	}
}
