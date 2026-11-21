using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

internal sealed partial class CanOpenNode
{
    /// <summary>Test seam: pause between an acknowledged write and the next record.</summary>
    internal Func<Task>? AfterConciseDcfWriteForTests { get; set; }

    private readonly record struct ConciseDcfWrite(ushort Index, byte Subindex, byte[] Value);

    public async Task DownloadConciseDcfAsync(byte peerNodeId, ReadOnlyMemory<byte> conciseDcf,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        CanOpenCobId.ValidateNodeId(peerNodeId);
        cancellationToken.ThrowIfCancellationRequested();
        // Snapshot and validate the complete stream before waiting or sending its first write.
        var writes = ParseConciseDcf(conciseDcf.ToArray(), _options.MaxSdoTransferBytes);
        if (writes.Count == 0) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _readerCts.Token);
        var token = lifetime.Token;
        var gate = PeerSdoChannel(peerNodeId);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            var binding = new PeerDescriptionBinding(CaptureBoundPeerDescription(peerNodeId),
                Array.Empty<(ushort, byte)>(), new HashSet<ushort>(writes.Select(w => w.Index)));
            // Configuration writes require an explicit description, including base objects.
            if (!binding.IsBound)
                throw new InvalidOperationException("Concise DCF download requires a bound peer EDS or DCF. Nothing was sent.");
            foreach (var write in writes)
            {
                EnsureConfigurationWrite(peerNodeId, write.Index, write.Subindex, binding);
            }
            foreach (var write in writes)
            {
                token.ThrowIfCancellationRequested();
                await SdoDownloadCoreAsync(peerNodeId, write.Index, write.Subindex, write.Value,
                    SdoTransferMode.Auto, token, transaction: binding).ConfigureAwait(false);
                if (AfterConciseDcfWriteForTests is { } pause) await pause().ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // CiA 302-3 v4.1.0 §3.3, Figure 1: count U32, then (index U16, sub U8, size U32, bytes).
    // Counts and sizes are bounded by the remaining stream before converting to signed lengths.
    private static List<ConciseDcfWrite> ParseConciseDcf(byte[] stream, int maxValueBytes)
    {
        if (stream.Length < 4) throw new InvalidDataException("Concise DCF is missing its entry count.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(stream);
        if (count > (uint)((stream.Length - 4) / 7))
            throw new InvalidDataException("Concise DCF entry count exceeds the available record headers.");
        var writes = new List<ConciseDcfWrite>();
        int offset = 4;
        for (uint entry = 0; entry < count; entry++)
        {
            if (stream.Length - offset < 7) throw new InvalidDataException("Concise DCF record header is truncated.");
            ushort index = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(offset));
            byte subindex = stream[offset + 2];
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(offset + 3));
            offset += 7;
            if (length > (uint)(stream.Length - offset)) throw new InvalidDataException("Concise DCF record data is truncated.");
            if (length > (uint)maxValueBytes) throw new InvalidDataException("Concise DCF record exceeds MaxSdoTransferBytes.");
            writes.Add(new ConciseDcfWrite(index, subindex, stream.AsSpan(offset, (int)length).ToArray()));
            offset += (int)length;
        }
        if (offset != stream.Length) throw new InvalidDataException("Concise DCF contains trailing bytes after its declared entries.");
        return writes;
    }
}
