namespace Edge.Atas.Hub;

using System.Collections.Generic;

using Edge.Atas.Core;

/// <summary>
/// One posting hub per instrument family per ATAS process. ES and MES are one price series, as are
/// NQ and MNQ, so two hubs in a family would post the same signal twice. ATAS loads the DLL once,
/// so a static table is shared by every chart.
/// </summary>
public static class HubClaim
{
	private static readonly object Lock = new();
	private static readonly Dictionary<string, object> Owners = new();

	public static string FamilyOf(InstrumentProfile p) => p.Name.EndsWith("NQ") ? "NQ" : "ES";

	public static bool TryClaim(string family, object owner)
	{
		lock (Lock)
		{
			if (Owners.TryGetValue(family, out var current) && !ReferenceEquals(current, owner))
				return false;

			Owners[family] = owner;
			return true;
		}
	}

	public static void Release(string family, object owner)
	{
		if (family is null)
			return;

		lock (Lock)
		{
			if (Owners.TryGetValue(family, out var current) && ReferenceEquals(current, owner))
				Owners.Remove(family);
		}
	}
}
