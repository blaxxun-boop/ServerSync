using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ServerSync;

[PublicAPI]
[HarmonyPatch]
public class VersionCheck
{
	private static readonly HashSet<VersionCheck> versionChecks = new();

	public string Name;

	private string? displayName;

	public string DisplayName
	{
		get => displayName ?? Name;
		set => displayName = value;
	}

	private string? currentVersion;

	public string CurrentVersion
	{
		get => currentVersion ?? "0.0.0";
		set => currentVersion = value;
	}

	private string? minimumRequiredVersion;

	public string MinimumRequiredVersion
	{
		get => minimumRequiredVersion ?? (ModRequired ? CurrentVersion : "0.0.0");
		set => minimumRequiredVersion = value;
	}

	public bool ModRequired = true;

	private string? ReceivedCurrentVersion;

	private string? ReceivedMinimumRequiredVersion;

	// Tracks which clients have passed the version check (only for servers).
	private readonly List<ZRpc> ValidatedClients = new();

	// Optional backing field to use ConfigSync values (will override other fields).
	private ConfigSync? ConfigSync;

	public VersionCheck(string name)
	{
		Name = name;
		ModRequired = true;
		versionChecks.Add(this);
	}

	public VersionCheck(ConfigSync configSync)
	{
		ConfigSync = configSync;
		Name = ConfigSync.Name;
		versionChecks.Add(this);
	}

	public void Initialize()
	{
		ReceivedCurrentVersion = null;
		ReceivedMinimumRequiredVersion = null;
		if (ConfigSync == null)
		{
			return;
		}
		Name = ConfigSync.Name;
		DisplayName = ConfigSync.DisplayName!;
		CurrentVersion = ConfigSync.CurrentVersion!;
		MinimumRequiredVersion = ConfigSync.MinimumRequiredVersion!;
		ModRequired = ConfigSync.ModRequired;
	}

	private bool IsVersionOk()
	{
		if (ReceivedMinimumRequiredVersion == null)
		{
			return !ModRequired;
		}
		var myVersionOk = new System.Version(CurrentVersion) >= new System.Version(ReceivedMinimumRequiredVersion);
		var otherVersionOk = new System.Version(ReceivedCurrentVersion) >= new System.Version(MinimumRequiredVersion);
		return myVersionOk && otherVersionOk;
	}
	private string ErrorClient()
	{
		if (ReceivedMinimumRequiredVersion == null)
		{
			return $"Mod {DisplayName} must not be installed.";
		}
		var myVersionOk = new System.Version(CurrentVersion) >= new System.Version(ReceivedMinimumRequiredVersion);
		if (myVersionOk)
		{
			return $"Mod {DisplayName} requires maximum {ReceivedCurrentVersion}. Installed is version {CurrentVersion}.";
		}
		else
		{
			return $"Mod {DisplayName} requires minimum {ReceivedMinimumRequiredVersion}. Installed is version {CurrentVersion}.";
		}
	}

	private string ErrorServer(ZRpc rpc)
	{
		return $"Disconnect: The client ({rpc.m_socket.GetHostName()}) doesn't have the correct {DisplayName} version {MinimumRequiredVersion}";
	}
	
	private string Error(ZRpc? rpc = null)
	{
		return rpc == null ? ErrorClient() : ErrorServer(rpc);
	}
	
	
	private static VersionCheck[] GetFailedClient()
	{
		return versionChecks.Where(check => !check.IsVersionOk()).ToArray();
	}
	
	private static VersionCheck[] GetFailedServer(ZRpc rpc)
	{
		return versionChecks.Where(check => !check.ValidatedClients.Contains(rpc)).ToArray();
	}
	
	private static VersionCheck[] GetFailed(ZRpc? rpc = null)
	{
		return rpc == null ? GetFailedClient() : GetFailedServer(rpc);
	}

	private static void Logout()
	{
		Game.instance.Logout();
		AccessTools.DeclaredField(typeof(ZNet), "m_connectionStatus").SetValue(null, ZNet.ConnectionStatus.ErrorVersion);
	}
	private static void DisconnectClient(ZRpc rpc)
	{
		rpc.Invoke("Error", new object[] { (int)ZNet.ConnectionStatus.ErrorVersion });
	}

	private static void CheckVersion(ZRpc rpc, ZPackage pkg)
	{
		foreach (var check in versionChecks)
		{
			var guid = pkg.ReadString();
			if (guid != check.Name)
			{
				continue;
			}
			var minimumRequiredVersion = pkg.ReadString();
			var currentVersion = pkg.ReadString();
			if (ZNet.instance.IsServer())
			{
				Debug.Log($"Received '{check.DisplayName} version {currentVersion} and minimum version {minimumRequiredVersion} from the client.");
			}
			else
			{
				Debug.Log($"Received '{check.DisplayName} version {currentVersion} and minimum version {minimumRequiredVersion} from the server.");
			}
			check.ReceivedMinimumRequiredVersion = minimumRequiredVersion;
			check.ReceivedCurrentVersion = currentVersion;
			if (ZNet.instance.IsServer() && check.IsVersionOk())
			{
				check.ValidatedClients.Add(rpc);
			}
		}
	}
	private static bool VerifyServer(ZNet znet, ZRpc rpc)
	{
		if (znet.IsServer())
		{
			return true;
		}
		var failedChecks = GetFailed();
		if (failedChecks.Length == 0)
		{
			return true;
		}
		foreach (var check in failedChecks)
		{
			Debug.LogWarning(check.Error());
		}
		Logout();
		return false;
	}
	private static bool VerifyClient(ZNet znet, ZRpc rpc)
	{
		if (!znet.IsServer())
		{
			return true;
		}
		var failedChecks = GetFailed(rpc);
		if (failedChecks.Length == 0)
		{
			return true;
		}
		foreach (var check in failedChecks)
		{
			Debug.LogWarning(check.Error(rpc));
		}
		DisconnectClient(rpc);
		return false;
	}

	[HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo)), HarmonyPrefix]
	private static bool RPC_PeerInfo(ZRpc rpc, ZNet __instance)
	{
		return VerifyServer(__instance, rpc) && VerifyClient(__instance, rpc);
	}

	[HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection)), HarmonyPrefix]
	private static void RegisterAndCheckVersion(ZNetPeer peer, ZNet __instance)
	{
		foreach (var check in versionChecks)
		{
			check.Initialize();
			peer.m_rpc.Register<ZPackage>($"VersionCheck_{check.Name}", new Action<ZRpc, ZPackage>(VersionCheck.CheckVersion));
			if (__instance.IsServer())
			{
				Debug.Log($"Sending {check.DisplayName} version {check.CurrentVersion} and minimum version {check.MinimumRequiredVersion} to the client");
			}
			else
			{
				Debug.Log($"Sending {check.DisplayName} version {check.CurrentVersion} and minimum version {check.MinimumRequiredVersion} to the server");
			}

			ZPackage zpackage = new ZPackage();
			zpackage.Write(check.Name);
			zpackage.Write(check.MinimumRequiredVersion);
			zpackage.Write(check.CurrentVersion);
			peer.m_rpc.Invoke($"VersionCheck_{check.Name}", new object[] { zpackage });
		}
	}

	[HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect)), HarmonyPrefix]
	private static void RemoveDisconnected(ZNetPeer peer, ZNet __instance)
	{
		if (!__instance.IsServer())
		{
			return;
		}
		foreach (var check in versionChecks)
		{
			check.ValidatedClients.Remove(peer.m_rpc);
		}
	}

	[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.ShowConnectError)), HarmonyPostfix]
	private static void ShowConnectionError(FejdStartup __instance)
	{
		if (!__instance.m_connectionFailedPanel.activeSelf)
		{
			return;
		}
		var failedChecks = GetFailed();
		if (failedChecks.Length == 0)
		{
			return;
		}
		var error = string.Join("\n", failedChecks.Select(check => check.Error()));
		__instance.m_connectionFailedError.text += "\n" + error;
	}
}
