using System;
using System.Reflection;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;

namespace CanKit.Pro.Tests.Infrastructure;

/// <summary>
/// xUnit class fixture that makes <c>virtual://</c> endpoints resolvable.
///
/// CanKit's registry discovers endpoint/factory registrations by scanning the assemblies that are
/// loaded into the AppDomain at the moment its lazy singleton is first built. Nothing in
/// CanKit.Pro references <c>CanKit.Adapter.Virtual</c> in code, so on a clean test run the JIT has
/// no reason to load it and every <c>CanBus.Open("virtual://...")</c> would fail with
/// "No endpoint handler registered". Forcing the load in a fixture that every bus-backed test
/// class depends on fixes that deterministically, before the first Open.
/// </summary>
public sealed class VirtualAdapterFixture
{
    /// <summary>Arbitration bitrate used by every bus these tests open.</summary>
    public const int Bitrate = 500_000;

    /// <summary>Data-phase bitrate for the CAN FD buses (ISO-TP over FD, mainly).</summary>
    public const int DataBitrate = 2_000_000;

    static VirtualAdapterFixture()
    {
        // Throwing here would fail every test with an unrelated fixture error, but so would the
        // first CanBus.Open — and that failure message is the more useful one.
        try
        {
#if NET5_0_OR_GREATER
            System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(
                new AssemblyName("CanKit.Adapter.Virtual"));
#else
            Assembly.Load(new AssemblyName("CanKit.Adapter.Virtual"));
#endif
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to force-load CanKit.Adapter.Virtual: {ex}");
        }
    }

    /// <summary>
    /// Opens a Classic-CAN bus on the loopback ("virtual") adapter. Each test uses its own session
    /// id, so buses opened by different tests never see each other's traffic.
    /// </summary>
    public static ICanBus Open(string session, int channel, ChannelWorkMode workMode = ChannelWorkMode.Normal)
        => CanBus.Open(
            $"virtual://{session}/{channel}",
            cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(Bitrate).SetWorkMode(workMode));

    /// <summary>A session id unique to one test, prefixed for readability in failure output.</summary>
    public static string NewSession(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
