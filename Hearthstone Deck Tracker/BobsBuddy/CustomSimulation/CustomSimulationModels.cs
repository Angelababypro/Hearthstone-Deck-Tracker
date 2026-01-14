using System.Collections.Generic;

namespace Hearthstone_Deck_Tracker.BobsBuddy
{
	internal sealed class SimOptions
	{
		public int Iterations { get; set; } = 10000;
		public int? TimeoutMs { get; set; }
		public int? ThreadCount { get; set; }
	}

	internal sealed class SimResult
	{
		public double Win { get; set; }
		public double Tie { get; set; }
		public double Lose { get; set; }
		public int Simulations { get; set; }
	}

	internal sealed class CustomBattleSnapshot
	{
		public CustomSideSnapshot Player { get; set; } = new CustomSideSnapshot();
		public CustomSideSnapshot Opponent { get; set; } = new CustomSideSnapshot();
		public int? DamageCap { get; set; }
		public IList<string>? AvailableRaces { get; set; }
		public string? AnomalyCardId { get; set; }
	}

	internal sealed class CustomSideSnapshot
	{
		public int? Health { get; set; }
		public int? Armor { get; set; }
		public int? Tier { get; set; }
		public int? DamageTaken { get; set; }
		public IList<CustomMinion>? Minions { get; set; }
	}

	internal sealed class CustomMinion
	{
		public string CardId { get; set; } = string.Empty;
		public int? Atk { get; set; }
		public int? Hp { get; set; }
		public bool? Golden { get; set; }
		public int? Tier { get; set; }
		public IList<string>? Tags { get; set; }
		public int? ScriptDataNum1 { get; set; }
		public int? ScriptDataNum2 { get; set; }
		public int? ScriptDataNum3 { get; set; }
	}
}
