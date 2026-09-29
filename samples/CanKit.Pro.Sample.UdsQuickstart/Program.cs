using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.IsoTp;
using CanKit.Pro.Uds;

// UDS quickstart: talk to a (simulated) car ECU the way a diagnostic tester does
// ================================================================================
//
// UDS (ISO 14229) is the language of car diagnostics: a *tester* (your program) sends a request
// such as "which VIN do you have?" to an *ECU* (a control unit), and the ECU answers — either
// with a positive response or with a negative one that carries a reason code (an "NRC").
// UDS messages are longer than a CAN frame, so they travel over ISO-TP (see the
// IsoTpQuickstart sample); the UDS client does the request/response bookkeeping for you:
// timeouts, "I'm still busy" answers, and turning negative responses into exceptions.
//
// This sample needs no car and no hardware. It
//   1. opens two virtual CAN buses wired together (tester side and ECU side),
//   2. starts a tiny *simulated* ECU on one of them (its code is at the bottom of this file —
//      skip it on a first read; with a real car you would not write this part at all),
//   3. lets the UDS client on the other bus switch diagnostic session, read the VIN,
//      and see what a refusal from the ECU looks like.
//
//   dotnet run --project samples/CanKit.Pro.Sample.UdsQuickstart
//   dotnet run --project samples/CanKit.Pro.Sample.UdsQuickstart -- --pro   (adds the extras)
//
// Words you will meet:
//   session  the ECU's operating mode. "Default" allows little; "Extended" unlocks more services.
//   SID      service identifier — the first byte of a request (0x10 session control, 0x22 read ...).
//   DID      data identifier — which value to read or write, e.g. 0xF190 is defined as the VIN.
//   NRC      negative response code — the ECU's reason for saying no.

var pro = args.Contains("--pro");

// ── 1. Two virtual buses ───────────────────────────────────────────────────────────────────
// Buses with the same session name in "virtual://<session>/<channel>" are wired together.
Step("1. Open two virtual CAN buses (tester and ECU)");
var session = $"uds-sample-{Guid.NewGuid():N}";
using var testerBus = OpenBus(session, channel: 0);
using var ecuBus = OpenBus(session, channel: 1);

// ── 2. ISO-TP channels ─────────────────────────────────────────────────────────────────────
// 0x7E0 / 0x7E8 is the classic tester -> engine-ECU / ECU -> tester ID pair.
Step("2. Open an ISO-TP channel on each bus (requests on 0x7E0, responses on 0x7E8)");
using var testerChannel = IsoTp.Open(testerBus, IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8));
using var ecuChannel = IsoTp.Open(ecuBus, IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0));

// ── 3. The simulated ECU ───────────────────────────────────────────────────────────────────
Step("3. Start the simulated ECU (see SimulatedEcu at the bottom of this file)");
var ecu = new SimulatedEcu(ecuChannel);
ecu.Start();

// ── 4. The UDS client ──────────────────────────────────────────────────────────────────────
// One client = one tester talking to one ECU. Create it once and keep it for the whole
// conversation. Disposing it (the 'using') stops its background work.
Step("4. Create the UDS client");
using var client = UdsClient.Create(testerChannel);
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));  // never wait forever

// ── 5. Switch session ──────────────────────────────────────────────────────────────────────
Step("5. Switch to the extended diagnostic session (service 0x10)");
await client.DiagnosticSessionControlAsync(UdsSessionType.Extended, cts.Token);
Console.WriteLine($"   active session is now 0x{client.CurrentSession:X2} (0x03 = extended)");

// ── 6. Read a value ────────────────────────────────────────────────────────────────────────
// ReadDataByIdentifierAsync returns just the data record; the DID and the 0x62 response byte
// are stripped for you.
Step("6. Read the VIN (service 0x22, data identifier 0xF190)");
var vin = await client.ReadDataByIdentifierAsync(0xF190, cts.Token);
Console.WriteLine($"   VIN = {Encoding.ASCII.GetString(vin)}");

// ── 7. A refusal is an exception ───────────────────────────────────────────────────────────
// Our fake ECU does not implement ECU reset, so it answers with a negative response.
// The client throws a UdsNegativeResponseException that says which service and why.
Step("7. Ask for something the ECU does not support (ECU reset, service 0x11)");
try
{
    await client.EcuResetAsync(UdsEcuResetType.HardReset, cts.Token);
}
catch (UdsNegativeResponseException ex)
{
    Console.WriteLine($"   refused: service {ex.RequestedService}, reason {ex.CodeName} " +
                      $"(NRC 0x{ex.Code:X2})");
}

Console.WriteLine();
Console.WriteLine("Done. Other failures have their own exceptions: UdsTimeoutException when the ECU");
Console.WriteLine("stays silent, UdsProtocolException when it answers something that makes no sense.");

if (!pro)
{
    Console.WriteLine("Run with '-- --pro' for slow ECUs, timeouts, security access and keep-alive.");
    ecu.Stop();
    return;
}

// ═══════════════════════════════════════════════════════════════════════════════════════════
// Pro extras (dotnet run ... -- --pro)
// ═══════════════════════════════════════════════════════════════════════════════════════════

// A client whose P2 (how long the ECU may take to start answering) is short, and whose P2*
// (how long it may take once it has said "still busy") is generous. Both are per client.
using var strictClient = UdsClient.Create(testerChannel, new UdsClientOptions
{
    P2ClientMax = TimeSpan.FromMilliseconds(200),
    P2StarClientMax = TimeSpan.FromSeconds(3),
});

// ── A. "I'm still working": NRC 0x78 ───────────────────────────────────────────────────────
// A slow ECU answers 0x78 (responsePending) instead of staying silent, then sends the real
// answer later. The client swaps P2 for the longer P2* and keeps waiting — your code sees one
// ordinary call that simply takes a while. The ECU below needs about 400 ms, twice P2.
Step("A. Slow ECU: reads a value that takes ~400 ms; the ECU answers 'pending' twice first");
{
    var clock = Stopwatch.StartNew();
    var record = await strictClient.ReadDataByIdentifierAsync(0xF1A0, cts.Token);
    Console.WriteLine($"   got '{Encoding.ASCII.GetString(record)}' after {clock.ElapsedMilliseconds} ms " +
                      "(P2 was 200 ms; the pending answers moved the deadline to P2*)");
}

// ── B. Silence is a UdsTimeoutException ────────────────────────────────────────────────────
Step("B. Silent ECU: ask for a value the simulated ECU never answers");
try
{
    await strictClient.ReadDataByIdentifierAsync(0xF1FF, cts.Token);
}
catch (UdsTimeoutException ex)
{
    Console.WriteLine($"   UdsTimeoutException: timer {ex.Timer} ({ex.Elapsed.TotalMilliseconds:F0} ms) " +
                      $"for service {ex.RequestedService}");
}

// ── C. Security access ─────────────────────────────────────────────────────────────────────
// The ECU sends a random "seed"; the tester proves it knows the secret by returning the matching
// "key". The algorithm is yours (usually a manufacturer secret); here the key is the seed
// XOR-ed with a constant. The client runs both requests back to back and does not let a
// TesterPresent or another call slip in between.
Step("C. Security access (service 0x27): unlock with a seed/key exchange");
await client.SecurityAccessAsync(requestSeedLevel: 0x01, computeKey: seed =>
{
    Console.WriteLine($"   ECU sent seed {BitConverter.ToString(seed)}");
    return seed.Select(b => (byte)(b ^ 0xA5)).ToArray();
}, cts.Token);
Console.WriteLine("   accepted — the ECU is unlocked");

try
{
    await client.SecurityAccessAsync(requestSeedLevel: 0x01, computeKey: seed => seed, cts.Token);
}
catch (UdsNegativeResponseException ex)
{
    Console.WriteLine($"   a wrong key is refused: {ex.CodeName} (NRC 0x{ex.Code:X2})");
}

// ── D. Writing a value ─────────────────────────────────────────────────────────────────────
Step("D. Write a value and read it back (service 0x2E)");
await client.WriteDataByIdentifierAsync(0xF1B0, Encoding.ASCII.GetBytes("Workshop 42"), cts.Token);
var stored = await client.ReadDataByIdentifierAsync(0xF1B0, cts.Token);
Console.WriteLine($"   ECU now holds '{Encoding.ASCII.GetString(stored)}'");

// ── E. Keep-alive ──────────────────────────────────────────────────────────────────────────
// Non-default sessions end on their own unless the ECU keeps hearing from the tester.
// StartTesterPresentKeepAlive sends TesterPresent (0x3E) in the background until disposed;
// by default it asks the ECU not to answer, so it adds no traffic in the other direction.
Step("E. Keep the session alive in the background (TesterPresent, every 100 ms)");
{
    var before = ecu.TesterPresentCount;
    using (client.StartTesterPresentKeepAlive(TimeSpan.FromMilliseconds(100)))
    {
        await Task.Delay(550, cts.Token);
    }
    Console.WriteLine($"   the ECU saw {ecu.TesterPresentCount - before} TesterPresent requests in ~550 ms");
}

ecu.Stop();
Console.WriteLine();
Console.WriteLine("Pro extras done. More services (routine control, download/upload) and the");
Console.WriteLine("options they depend on are described in src/CanKit.Pro.Uds/README.md.");

static ICanBus OpenBus(string session, int channel) =>
    CanBus.Open($"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(500_000));

static void Step(string title) => Console.WriteLine($"\n{title}");

// ───────────────────────────────────────────────────────────────────────────────────────────
// The simulated ECU. Only here so that the sample runs standalone — a real ECU is a real
// device on the bus. It receives one request at a time, looks at the service ID (first byte)
// and sends back a positive response (SID + 0x40) or a negative one (0x7F, SID, NRC).
// ───────────────────────────────────────────────────────────────────────────────────────────
internal sealed class SimulatedEcu
{
    private const byte NegativeResponse = 0x7F;
    private readonly IIsoTpChannel _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _vin = Encoding.ASCII.GetBytes("WBAEX00000EXAMPLE");
    private byte[] _workshopCode = Encoding.ASCII.GetBytes("factory");
    private byte[] _lastSeed = Array.Empty<byte>();
    private int _testerPresentCount;

    public SimulatedEcu(IIsoTpChannel channel) => _channel = channel;

    public int TesterPresentCount => Volatile.Read(ref _testerPresentCount);

    public void Start() => _ = Task.Run(RunAsync);

    public void Stop() => _stop.Cancel();

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var request = await _channel.ReceiveAsync(_stop.Token);
                if (request.Length == 0) continue;
                await HandleAsync(request);
            }
        }
        catch (OperationCanceledException)
        {
            // The sample is over.
        }
    }

    private async Task HandleAsync(byte[] request)
    {
        switch (request[0])
        {
            case 0x10 when request.Length >= 2:                       // DiagnosticSessionControl
                await Send(0x50, request[1]);
                break;

            case 0x22 when request.Length >= 3:                       // ReadDataByIdentifier
                await HandleReadAsync((ushort)((request[1] << 8) | request[2]));
                break;

            case 0x2E when request.Length >= 3:                       // WriteDataByIdentifier
                if (request[1] == 0xF1 && request[2] == 0xB0)
                {
                    _workshopCode = request.AsSpan(3).ToArray();
                    await Send(0x6E, request[1], request[2]);
                }
                else await Send(NegativeResponse, request[0], 0x31);  // requestOutOfRange
                break;

            case 0x27 when request.Length >= 2 && request[1] == 0x01: // SecurityAccess: seed
                _lastSeed = new byte[] { 0x12, 0x34, 0x56, 0x78 };
                await Send(new byte[] { 0x67, 0x01 }.Concat(_lastSeed).ToArray());
                break;

            case 0x27 when request.Length >= 3 && request[1] == 0x02: // SecurityAccess: key
                var expected = _lastSeed.Select(b => (byte)(b ^ 0xA5));
                if (request.Skip(2).SequenceEqual(expected)) await Send(0x67, 0x02);
                else await Send(NegativeResponse, request[0], 0x35);  // invalidKey
                break;

            case 0x3E:                                                // TesterPresent
                Interlocked.Increment(ref _testerPresentCount);
                if (request.Length >= 2 && (request[1] & 0x80) == 0)  // bit 7 = "do not answer"
                    await Send(0x7E, request[1]);
                break;

            default:                                                  // everything else
                await Send(NegativeResponse, request[0], 0x11);       // serviceNotSupported
                break;
        }
    }

    private async Task HandleReadAsync(ushort did)
    {
        switch (did)
        {
            case 0xF190:                                              // the VIN
                await Send(new byte[] { 0x62, 0xF1, 0x90 }.Concat(_vin).ToArray());
                break;

            case 0xF1B0:                                              // writable workshop code
                await Send(new byte[] { 0x62, 0xF1, 0xB0 }.Concat(_workshopCode).ToArray());
                break;

            case 0xF1A0:                                              // a slow value
                await Send(NegativeResponse, 0x22, 0x78);             // "responsePending" ...
                await Task.Delay(200);
                await Send(NegativeResponse, 0x22, 0x78);             // ... twice ...
                await Task.Delay(200);
                await Send(new byte[] { 0x62, 0xF1, 0xA0 }.Concat(Encoding.ASCII.GetBytes("CAL-V2")).ToArray());
                break;

            case 0xF1FF:                                              // the ECU never answers
                break;

            default:
                await Send(NegativeResponse, 0x22, 0x31);             // requestOutOfRange
                break;
        }
    }

    private Task Send(params byte[] response) => _channel.SendAsync(response, _stop.Token);
}
