using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Common;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.RawCan;

namespace CanKit.Pro.CanOpenConformance;

/// <summary>
/// The bus, the tool's own CANopen node and the LSS master, for the length of one run. Everything
/// shares one <see cref="ICanBusService"/>: the node, the LSS master and the raw taps the
/// observation checks use all read the same interface, which is what the L2 demultiplexer is for
/// — a second bus handle on one adapter channel is not something every driver allows.
/// </summary>
internal sealed class Session : IDisposable
{
    private ILssMaster? _lss;

    private Session(RunOptions options, ICanBus bus, ICanBusService service, ICanOpenNode client)
    {
        Options = options;
        Bus = bus;
        Service = service;
        Client = client;
        BackgroundExceptions = new List<string>();

        // A background failure inside the node's actor is otherwise invisible: it would reach
        // nothing but an unobserved task. Collected and reported with the run, because a device
        // that makes the stack throw in the background is exactly what a conformance run is for.
        client.BackgroundExceptionOccurred += (_, ex) =>
        {
            lock (BackgroundExceptions) BackgroundExceptions.Add($"{ex.GetType().Name}: {ex.Message}");
        };
        service.BackgroundExceptionOccurred += (_, ex) =>
        {
            lock (BackgroundExceptions) BackgroundExceptions.Add($"bus service: {ex.GetType().Name}: {ex.Message}");
        };
    }

    public RunOptions Options { get; }

    public ICanBus Bus { get; }

    public ICanBusService Service { get; }

    /// <summary>The tool's own node — a CANopen manager, not a device: <see
    /// cref="CanOpenNodeProfile.Tool"/> leaves the self-start bits alone so that opening it does
    /// not start the devices on a bus the operator only wanted to look at.</summary>
    public ICanOpenNode Client { get; }

    /// <summary>The device description bound for the target, when --eds gave one.</summary>
    public CanOpenDeviceDescription? TargetDescription { get; private set; }

    /// <summary>Why the description could not be bound, when one was given and it failed; null
    /// otherwise. The eds group reports it, so a bad file is a finding about the file.</summary>
    public string? DescriptionError { get; private set; }

    public List<string> BackgroundExceptions { get; }

    /// <summary>The node under test. Checks that need it are only reached once <see
    /// cref="RunOptions.TargetNode"/> is set, which <see cref="Program"/> enforces per group.</summary>
    public byte Target => Options.TargetNode
        ?? throw new InvalidOperationException("This check needs --node.");

    /// <summary>The LSS master, opened on first use. Opening it is not itself a bus action —
    /// an LSS master sends nothing until a service is called.</summary>
    public ILssMaster Lss => _lss ??= CanOpenLss.OpenMaster(Service, new LssMasterOptions
    {
        ResponseTimeout = TimeSpan.FromMilliseconds(Math.Max(100, Options.SdoTimeoutMs)),
    }, leaveOpen: true);

    /// <summary>
    /// Opens the bus and the tool's node. The adapter assemblies are loaded first: CanKit resolves
    /// an adapter from the assemblies loaded when the first bus is opened, and an adapter whose
    /// vendor driver is absent still loads — the driver is only needed for the open itself.
    /// </summary>
    public static Session Open(RunOptions options, out string? error)
    {
        LoadAdapterAssemblies();
        ICanBus? bus = null;
        CanBusService? service = null;
        try
        {
            bus = CanBus.Open(options.Endpoint, cfg => Configure(cfg, options));
            service = new CanBusService(bus);
            var client = CanOpen.OpenNode(service, options.ClientNode, new CanOpenNodeOptions
            {
                Profile = CanOpenNodeProfile.Tool,
                SdoTimeout = TimeSpan.FromMilliseconds(options.SdoTimeoutMs),
            }, leaveOpen: true);

            var session = new Session(options, bus, service, client);
            error = null;
            return session;
        }
        catch (Exception ex)
        {
            service?.Dispose();
            bus?.Dispose();
            error = $"{options.Endpoint}: {ex.GetType().Name}: {ex.Message}";
            throw new OpenFailedException(error, ex);
        }
    }

    /// <summary>
    /// Binds the EDS or DCF for the target. This is not a convenience: the stack's peer-SDO gate
    /// transfers only 1000h:00, 1001h:00 and 1018h:00-04 to a node with no description bound, so
    /// without a file here most of this tool's checks have nothing they are allowed to ask.
    /// </summary>
    /// <returns>
    /// Null when the file was bound or none was given, else why it was not. Deliberately not an
    /// exception out of <see cref="Open"/>: the file is read after the bus is open, so a malformed
    /// one used to surface as "the bus could not be opened" and be attributed to the adapter or its
    /// driver. The bus is fine in that case and the run's own input is not, which is a different
    /// finding for a different person. Found in review of #388.
    /// </returns>
    public string? BindDescription()
    {
        if (Options.EdsPath is null || Options.TargetNode is not { } target) return null;
        try
        {
            TargetDescription = CanOpenDeviceDescription.Load(Options.EdsPath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return DescriptionError = $"{System.IO.Path.GetFileName(Options.EdsPath)} could not be read: {ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            Client.BindPeerDeviceDescription(target, TargetDescription);
        }
        catch (ArgumentException ex)
        {
            // A DCF commissioned for another node-id is the commonest of these, and it is the
            // operator's to fix by passing the right file or the right --node.
            TargetDescription = null;
            return DescriptionError = $"{System.IO.Path.GetFileName(Options.EdsPath)} cannot be bound for node {target}: {ex.Message}";
        }

        return null;
    }

    /// <summary>Whether the bound description declares the pair, i.e. whether an SDO to it will
    /// get past the gate. With no description, the three mandatory base objects.</summary>
    public bool MayTransfer(ushort index, byte subindex)
        => TargetDescription is { } description
            ? description.Contains(index, subindex)
            : PeerSdoAccessException.IsAllowedWithoutPeerDescription(index, subindex);

    /// <summary>The standard reason a check gives when the gate blocks what it wanted to read.</summary>
    public string GateReason(ushort index, byte subindex)
    {
        // A bind that failed is a third case, and it used to read as the first: the operator was
        // told to pass --eds when they had passed one the loader could not use. Found in review
        // of #388.
        if (DescriptionError is { } problem)
            return $"0x{index:X4}:{subindex:X2} is blocked because the device description could not be used: {problem}";

        return TargetDescription is null
            ? $"0x{index:X4}:{subindex:X2} needs --eds: with no device description the stack transfers only 1000h:00, 1001h:00 and 1018h:00-04."
            : $"0x{index:X4}:{subindex:X2} is not declared by {System.IO.Path.GetFileName(Options.EdsPath)}; the device may still have it, but this run may not ask.";
    }

    private static void Configure(IBusInitOptionsConfigurator cfg, RunOptions options)
    {
        if (options.UseFd)
            cfg.Fd(options.Bitrate, options.DataBitrate).SetProtocolMode(CanProtocolMode.CanFd);
        else
            cfg.Baud(options.Bitrate).SetProtocolMode(CanProtocolMode.Can20);

        // Normal, not ListenOnly, even in the read tier: an SDO upload is a read of the device and
        // still needs to transmit, and a listen-only interface acknowledges nothing, which on a
        // two-node bus stops the device from transmitting at all. What the read tier promises is
        // that nothing the device holds is changed, not that no frame leaves this interface.
        cfg.SetWorkMode(ChannelWorkMode.Normal);
        cfg.EnableErrorInfo();
    }

    private static void LoadAdapterAssemblies()
    {
        foreach (var name in new[] { "CanKit.Adapter.Virtual", "CanKit.Adapter.PCAN", "CanKit.Adapter.Kvaser", "CanKit.Adapter.Vector" })
        {
            try
            {
                Assembly.Load(new AssemblyName(name));
            }
            catch (Exception ex) when (ex is System.IO.FileNotFoundException or System.IO.FileLoadException or BadImageFormatException)
            {
                Console.WriteLine($"  note: adapter assembly {name} could not be loaded ({ex.GetType().Name}).");
            }
        }
    }

    /// <summary>
    /// Releases what the run owns from the top of the stack down: the LSS master and the node
    /// first, then the demultiplexer they read through, then the bus underneath it. The order is
    /// the point. An earlier version walked the ownership list backwards, which closed the bus
    /// while the node and the CanBusService still held it — teardown could then throw or hang
    /// after everything had been measured but before the report was written. Found in review
    /// of #388.
    /// </summary>
    public void Dispose()
    {
        foreach (var disposable in Layers())
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                // Teardown must not lose a finished run's report, so the remaining layers are
                // still disposed — but it must not lose the failure either. Printing and
                // swallowing meant the caller could never see it: the report came out clean and
                // the process exited 0 although the node, the service or the bus had failed to
                // shut down. It goes on the list the run already reads for background exceptions,
                // which is what the exit code and the report's error section are drawn from.
                // Found in review of #388.
                Console.WriteLine($"  note: disposing {disposable.GetType().Name} threw {ex.GetType().Name}: {ex.Message}");
                lock (BackgroundExceptions)
                {
                    BackgroundExceptions.Add(
                        $"{ex.GetType().Name} while disposing {disposable.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>What the run owns, outermost first.</summary>
    private IEnumerable<IDisposable> Layers()
    {
        if (_lss is { } lss) yield return lss;
        yield return Client;
        yield return Service;
        yield return Bus;
    }
}

/// <summary>Opening the bus failed; the message already names the endpoint and the driver error.</summary>
internal sealed class OpenFailedException : Exception
{
    public OpenFailedException(string message, Exception inner) : base(message, inner) { }
}
