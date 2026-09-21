using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen;

/// <summary>
/// SDO block-transfer (CiA 301 §7.2.4.3.15, FR-CO-004) partial of <see cref="CanOpenNode"/>.
/// Implements both client and server sides for block download and block upload, using the
/// same actor loop / deadline scheduler / <see cref="CanKit.Pro.RawCan.ICanBusService"/> as
/// the rest of the node.
/// </summary>
/// <remarks>
/// <para>Threading: every state read/write in this file happens on the actor loop. Public entry
/// points (<c>BeginSdoBlock*</c>) are only ever invoked from posted actor actions, and
/// incoming-frame handlers are called from <see cref="CanOpenNode.HandleIncoming"/> which is
/// itself a posted actor action.</para>
/// <para>Session lifecycle mirrors the classical SDO client / server: at most one client-side
/// transfer per remote server (keyed by node-id), and at most one server-side transfer for
/// our own OD. A stale server-side transfer is superseded on any fresh initiate (CiA 301
/// §7.2.4.3.4) — matching what the plain SDO server already does.</para>
/// </remarks>
internal sealed partial class CanOpenNode
{
    // =========================================================================================
    // Client — block download (we send our payload byte-stream to the peer's OD).
    // =========================================================================================
    private void BeginSdoBlockDownload(byte serverNodeId, ushort index, byte subindex,
        byte[] payload, TaskCompletionSource<byte[]> tcs)
    {
        if (_disposed != 0)
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(CanOpenNode)));
            return;
        }
        if (tcs.Task.IsCompleted) return;
        if (_sdoClients.ContainsKey(serverNodeId) || _sdoBlockClients.ContainsKey(serverNodeId))
        {
            tcs.TrySetException(new InvalidOperationException(
                $"An SDO transfer with server 0x{serverNodeId:X2} is already in flight."));
            return;
        }
        if (payload.Length > _options.MaxSdoTransferBytes)
        {
            tcs.TrySetException(new SdoAbortException(index, subindex, SdoAbortCode.OutOfMemory,
                SdoAbortOrigin.Local));
            return;
        }

        var session = new SdoBlockClientSession(serverNodeId, index, subindex,
            isDownload: true, payload, tcs, _options.SdoBlockCrcSupported);
        _sdoBlockClients[serverNodeId] = session;
        session.Deadline = _deadlines.Arm(_options.SdoTimeout,
            () => OnSdoBlockClientTimeout(serverNodeId));

        var init = SdoBlockFrames.BuildBlockDownloadInit(index, subindex,
            clientCrcSupported: session.LocalCrcSupported,
            sizeIndicated: true,
            totalSize: (uint)payload.Length);
        _ = SendControlFrame(CanOpenCobId.SdoRx(serverNodeId), init);
    }

    // =========================================================================================
    // Client — block upload (we ask the peer to stream its OD value back to us).
    // =========================================================================================
    private void BeginSdoBlockUpload(byte serverNodeId, ushort index, byte subindex,
        TaskCompletionSource<byte[]> tcs)
    {
        if (_disposed != 0)
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(CanOpenNode)));
            return;
        }
        if (tcs.Task.IsCompleted) return;
        if (_sdoClients.ContainsKey(serverNodeId) || _sdoBlockClients.ContainsKey(serverNodeId))
        {
            tcs.TrySetException(new InvalidOperationException(
                $"An SDO transfer with server 0x{serverNodeId:X2} is already in flight."));
            return;
        }

        var session = new SdoBlockClientSession(serverNodeId, index, subindex,
            isDownload: false, payload: null, tcs, _options.SdoBlockCrcSupported);
        session.LocalBlockSize = _options.SdoBlockSize;
        _sdoBlockClients[serverNodeId] = session;
        session.Deadline = _deadlines.Arm(_options.SdoTimeout,
            () => OnSdoBlockClientTimeout(serverNodeId));

        // pst=0 disables the CiA 301 fallback to segmented transfer. We do not currently
        // implement the fallback and setting pst=0 keeps peers from ever forcing us into it.
        var init = SdoBlockFrames.BuildBlockUploadInit(index, subindex,
            clientCrcSupported: session.LocalCrcSupported,
            blockSize: session.LocalBlockSize,
            pst: 0);
        _ = SendControlFrame(CanOpenCobId.SdoRx(serverNodeId), init);
    }

    private void OnSdoBlockClientTimeout(byte serverNodeId)
    {
        if (!_sdoBlockClients.TryGetValue(serverNodeId, out var session)) return;
        _sdoBlockClients.Remove(serverNodeId);
        session.Deadline?.Dispose();
        _ = SendControlFrame(CanOpenCobId.SdoRx(serverNodeId),
            SdoFrames.BuildAbort(session.Index, session.Subindex, (uint)SdoAbortCode.SdoProtocolTimedOut));
        session.Tcs.TrySetException(new SdoAbortException(session.Index, session.Subindex,
            SdoAbortCode.SdoProtocolTimedOut, SdoAbortOrigin.Local));
    }

    private void RearmBlockClient(SdoBlockClientSession session, byte serverNodeId)
    {
        var deadline = session.Deadline;
        if (deadline is null || deadline.IsExpired || deadline.IsCancelled || !deadline.Rearm(_options.SdoTimeout))
        {
            deadline?.Dispose();
            session.Deadline = _deadlines.Arm(_options.SdoTimeout, () => OnSdoBlockClientTimeout(serverNodeId));
        }
    }

    private void AbortBlockClient(SdoBlockClientSession session, SdoAbortCode code)
    {
        _sdoBlockClients.Remove(session.ServerNodeId);
        session.Deadline?.Dispose();
        _ = SendControlFrame(CanOpenCobId.SdoRx(session.ServerNodeId),
            SdoFrames.BuildAbort(session.Index, session.Subindex, (uint)code));
        session.Tcs.TrySetException(new SdoAbortException(session.Index, session.Subindex, code,
            SdoAbortOrigin.Local));
    }

    /// <summary>
    /// Handles a frame arriving on <c>0x580 + serverNodeId</c> when a block-transfer client
    /// session against that server is open. Returns true if this handler consumed the frame,
    /// false if the caller should fall back to the classical <c>HandleSdoClientResponse</c>.
    /// </summary>
    private bool HandleSdoClientResponseBlock(byte serverNodeId, byte[] data)
    {
        if (!_sdoBlockClients.TryGetValue(serverNodeId, out var session)) return false;
        if (data.Length == 0) return true; // consume — nothing to parse

        // Pad short DLC frames back to 8 bytes for parsing, matching the classic SDO client
        // path's behaviour with DLC-stripping ECUs.
        if (data.Length < 8)
        {
            var padded = new byte[8];
            Buffer.BlockCopy(data, 0, padded, 0, data.Length);
            data = padded;
        }

        byte cs = data[0];

        // Explicit abort from the peer always wins, regardless of phase.
        if (cs == SdoFrames.CsAbort)
        {
            var (idx, sub) = SdoFrames.ReadIndex(data);
            uint code = SdoFrames.ReadAbortCode(data);
            if (idx != session.Index || sub != session.Subindex) return true;
            _sdoBlockClients.Remove(serverNodeId);
            session.Deadline?.Dispose();
            session.Tcs.TrySetException(new SdoAbortException(idx, sub, code,
                $"Peer server 0x{serverNodeId:X2} aborted SDO block transfer 0x{idx:X4}:{sub:X2} with code 0x{code:X8}."));
            return true;
        }

        // Attribution before the deadline is touched (#18). While the initiate response is
        // awaited, every legitimate frame carries the multiplexer in bytes 1..3 — the block
        // download initiate response (CiA 301 §7.2.4.3.9, Figure 27) and the block upload
        // initiate response (§7.2.4.3.13, Figure 31) alike — so a frame naming another object
        // is somebody else's response (a late answer to an earlier request of ours, or the reply
        // to a second client on the same server) and is consumed without effect: no abort, no
        // phase change, no re-arm. The later phases exchange sub-block ACKs and end frames,
        // which carry no multiplexer (Figures 28, 29, 32, 33) and are matched by phase alone.
        if (session.Phase == SdoBlockClientPhase.AwaitInitResponse)
        {
            var (idx, sub) = SdoFrames.ReadIndex(data);
            if (idx != session.Index || sub != session.Subindex) return true;
        }

        RearmBlockClient(session, serverNodeId);

        if (session.IsDownload)
        {
            return HandleBlockDownloadClientFrame(session, data, cs);
        }
        return HandleBlockUploadClientFrame(session, data, cs);
    }

    private bool HandleBlockDownloadClientFrame(SdoBlockClientSession session, byte[] data, byte cs)
    {
        switch (session.Phase)
        {
            case SdoBlockClientPhase.AwaitInitResponse:
                // Expect 0xA0 | (sc<<2). Reject anything else.
                if ((cs & 0xE3) != ScsInitResponseMaskDownload)
                {
                    AbortBlockClient(session, SdoAbortCode.CommandSpecifierInvalid);
                    return true;
                }
                {
                    byte serverBlkSize = data[4];
                    if (serverBlkSize is < 1 or > 127)
                    {
                        AbortBlockClient(session, SdoAbortCode.InvalidBlockSize);
                        return true;
                    }
                    // CRC is exchanged only when both endpoints advertised support (CiA 301).
                    session.CrcActive = session.LocalCrcSupported && SdoBlockFrames.ReadCrcSupportedBit(cs);
                    session.NegotiatedBlockSize = serverBlkSize;
                    session.Phase = SdoBlockClientPhase.SendingSegments;
                    SendNextBlockDownloadSubBlock(session);
                }
                return true;

            case SdoBlockClientPhase.AwaitSubBlockAck:
                if (cs != ScsBlockDownloadSubBlockAck)
                {
                    AbortBlockClient(session, SdoAbortCode.CommandSpecifierInvalid);
                    return true;
                }
                {
                    var (ackseq, nextBlkSize) = SdoBlockFrames.ReadSubBlockAck(data);
                    // ackseq counts cumulatively from the start of the current sub-block
                    // (CiA 301 §7.2.4.3.15). Acknowledging more than we sent is a protocol
                    // violation; acknowledging less asks us to retransmit from ackseq + 1.
                    if (ackseq > session.SubBlockLastSeqno)
                    {
                        AbortBlockClient(session, SdoAbortCode.General);
                        return true;
                    }
                    if (nextBlkSize is < 1 or > 127)
                    {
                        AbortBlockClient(session, SdoAbortCode.InvalidBlockSize);
                        return true;
                    }
                    if (ackseq < session.SubBlockLastSeqno)
                    {
                        // Partial ACK: rewind to the first unconfirmed segment and retransmit
                        // from there with the ORIGINAL sub-block seqnos, instead of aborting.
                        // The advertised blksize is for "the following block" (§7.2.4.3.15): the
                        // current sub-block keeps its bound through the retransmission, or a
                        // size below ackseq + 1 would leave nothing to resend and the transfer
                        // waiting for a confirm that cannot come (#134). The peer restates the
                        // size with the confirm that completes this sub-block.
                        // Bounded by CanOpenNodeOptions.SdoBlockMaxRetransmissions against
                        // peers that never confirm progress (0 restores the old abort behavior).
                        if (++session.Retransmissions > _options.SdoBlockMaxRetransmissions)
                        {
                            AbortBlockClient(session, SdoAbortCode.General);
                            return true;
                        }
                        // Confirmed segments are always full 7-byte chunks: a short final
                        // segment can only sit at the tail (unconfirmed in a partial ACK).
                        session.Offset = session.SubBlockStartOffset + ackseq * 7;
                        session.ResumeSeqno = (byte)(ackseq + 1);
                        session.Phase = SdoBlockClientPhase.SendingSegments;
                        SendNextBlockDownloadSubBlock(session);
                        return true;
                    }
                    // Full confirmation of the current sub-block: the next one restarts at 1
                    // and is the first to use the advertised size.
                    session.NegotiatedBlockSize = nextBlkSize;
                    session.ResumeSeqno = 1;
                    if (session.Offset >= session.Payload!.Length)
                    {
                        SendBlockDownloadEnd(session);
                    }
                    else
                    {
                        session.Phase = SdoBlockClientPhase.SendingSegments;
                        SendNextBlockDownloadSubBlock(session);
                    }
                }
                return true;

            case SdoBlockClientPhase.AwaitEndResponse:
                if (cs != ScsBlockDownloadEndResponse)
                {
                    AbortBlockClient(session, SdoAbortCode.CommandSpecifierInvalid);
                    return true;
                }
                _sdoBlockClients.Remove(session.ServerNodeId);
                session.Deadline?.Dispose();
                session.Tcs.TrySetResult(Array.Empty<byte>());
                return true;

            default:
                AbortBlockClient(session, SdoAbortCode.CommandSpecifierInvalid);
                return true;
        }
    }

    private bool HandleBlockUploadClientFrame(SdoBlockClientSession session, byte[] data, byte cs)
    {
        switch (session.Phase)
        {
            case SdoBlockClientPhase.AwaitInitResponse:
                // Expect scs=6 (0xC0 base) with optional sc / s bits.
                if ((cs & 0xE1) != ScsInitResponseMaskUpload)
                {
                    AbortBlockClient(session, SdoAbortCode.CommandSpecifierInvalid);
                    return true;
                }
                {
                    uint declared = SdoBlockFrames.ReadUploadTotalSize(data);
                    if (declared > (uint)_options.MaxSdoTransferBytes)
                    {
                        AbortBlockClient(session, SdoAbortCode.OutOfMemory);
                        return true;
                    }
                    session.CrcActive = session.LocalCrcSupported && SdoBlockFrames.ReadCrcSupportedBit(cs);
                    session.DeclaredTotalSize = declared;
                    session.Payload = declared > 0 ? new byte[declared] : Array.Empty<byte>();
                    session.Offset = 0;
                    session.NextExpectedSeq = 1;
                    session.SubBlockDamaged = false;
                    session.Phase = SdoBlockClientPhase.ReceivingSegments;

                    // Tell the server to begin streaming segments.
                    _ = SendControlFrame(CanOpenCobId.SdoRx(session.ServerNodeId),
                        SdoBlockFrames.BuildEndResponse(SdoBlockFrames.CcsBlockUploadStart));
                }
                return true;

            case SdoBlockClientPhase.ReceivingSegments:
                // A segment frame (byte 0 = (c<<7)|seq). CiA 301 lets the "end of block"
                // frame come only *after* we have ACKed the last sub-block, so during
                // ReceivingSegments any incoming frame is a segment. The dispatcher in
                // HandleIncoming already prevented block-server segment values from being
                // decoded as regular SDO responses.
                return HandleBlockUploadSegment(session, data, cs);

            case SdoBlockClientPhase.AwaitEnd:
                // Expect 0xC1 | (n<<2) with CRC in bytes 1..2.
                if ((cs & 0xE3) != ScsBlockUploadEndMask)
                {
                    AbortBlockClient(session, SdoAbortCode.CommandSpecifierInvalid);
                    return true;
                }
                {
                    byte n = SdoBlockFrames.ReadEndUnusedBytes(cs);
                    ushort peerCrc = SdoBlockFrames.ReadEndCrc(data);
                    // The last stashed segment held 7 bytes of raw data; drop n of them.
                    if (n > 0)
                    {
                        if (session.Offset < n)
                        {
                            AbortBlockClient(session, SdoAbortCode.General);
                            return true;
                        }
                        session.Offset -= n;
                    }

                    if (session.DeclaredTotalSize > 0 && session.Offset != session.DeclaredTotalSize)
                    {
                        AbortBlockClient(session,
                            session.Offset > session.DeclaredTotalSize
                                ? SdoAbortCode.LengthTooHigh
                                : SdoAbortCode.LengthTooLow);
                        return true;
                    }
                    // With n applied the data must be within the cap (#59); a declared size was
                    // capped at the initiate response, so this bites for unbounded uploads only.
                    if (session.Offset > _options.MaxSdoTransferBytes)
                    {
                        AbortBlockClient(session, SdoAbortCode.OutOfMemory);
                        return true;
                    }

                    var final = new byte[session.Offset];
                    Buffer.BlockCopy(session.Payload!, 0, final, 0, session.Offset);

                    if (session.CrcActive)
                    {
                        var localCrc = SdoBlockFrames.ComputeCrc16Xmodem(final);
                        if (localCrc != peerCrc)
                        {
                            AbortBlockClient(session, SdoAbortCode.CrcError);
                            return true;
                        }
                    }

                    // Acknowledge end and complete the transfer.
                    _ = SendControlFrame(CanOpenCobId.SdoRx(session.ServerNodeId),
                        SdoBlockFrames.BuildEndResponse(SdoBlockFrames.CcsBlockUploadEndResponse));
                    _sdoBlockClients.Remove(session.ServerNodeId);
                    session.Deadline?.Dispose();
                    session.Tcs.TrySetResult(final);
                }
                return true;

            default:
                AbortBlockClient(session, SdoAbortCode.CommandSpecifierInvalid);
                return true;
        }
    }

    private bool HandleBlockUploadSegment(SdoBlockClientSession session, byte[] data, byte cs)
    {
        byte seq = (byte)(cs & 0x7F);
        bool last = (cs & 0x80) != 0;

        // The upload receiver mirrors the download server's rules (HandleBlockDownloadServerSegment)
        // with the client's own sub-block confirm (CiA 301 §7.2.4.3.14, Figure 32): the seqno is
        // "0 < seqno < 128" and never past the blksize we asked for.
        if (seq == 0 || seq > session.LocalBlockSize)
        {
            AbortBlockClient(session, SdoAbortCode.InvalidSequenceNumber);
            return true;
        }

        if (!session.SubBlockDamaged && seq == session.NextExpectedSeq)
        {
            // Same boundary rule as HandleBlockDownloadServerSegment (#59): the cap is on the
            // data, the last segment's data length is unknown until the end frame's "n"
            // (CiA 301 §7.2.4.3.15), so a segment is refused only when the cap is already full,
            // and the AwaitEnd handler checks the trimmed length. Refusing whenever fewer than
            // seven bytes of cap remained aborted an upload of exactly MaxSdoTransferBytes at
            // its last segment.
            if (session.Offset >= _options.MaxSdoTransferBytes)
            {
                AbortBlockClient(session, SdoAbortCode.OutOfMemory);
                return true;
            }
            if (session.Payload!.Length - session.Offset < 7)
            {
                // Grow when the declared size was 0 (unbounded) or when the payload was under-declared.
                var grown = new byte[session.Offset + 7];
                Buffer.BlockCopy(session.Payload, 0, grown, 0, session.Payload.Length);
                session.Payload = grown;
            }
            // Copy the full 7 data bytes; unused bytes in the *last* segment are trimmed off later
            // using the "n" field from the end-of-block frame.
            Buffer.BlockCopy(data, 1, session.Payload, session.Offset, 7);
            session.Offset += 7;
            session.NextExpectedSeq = (byte)(seq + 1);
        }
        else
        {
            // A lost or reordered segment: ignore the rest of the sub-block and let the one
            // confirm at its end carry ackseq, "sequence number of last segment that was received
            // successfully during the last block upload" (§7.2.4.3.14); the server resumes at
            // ackseq + 1 (#39). Answering each further segment with its own confirm was the same
            // storm as on the download server.
            session.SubBlockDamaged = true;
        }

        // The sub-block ends with the segment numbered blksize or with c = 1, recognised on the
        // sender's numbering so a damaged sub-block ends where the sender ends it.
        if (last || seq >= session.LocalBlockSize)
        {
            var ack = SdoBlockFrames.BuildSubBlockAck(SdoBlockFrames.CcsBlockUploadSubBlockAck,
                lastAckedSeq: (byte)(session.NextExpectedSeq - 1),
                nextBlockSize: session.LocalBlockSize);
            _ = SendControlFrame(CanOpenCobId.SdoRx(session.ServerNodeId), ack);
            if (session.SubBlockDamaged)
            {
                // Partial confirm: NextExpectedSeq stays where the gap was; the server resends
                // from there with the original numbering (as this package's upload server does).
                session.SubBlockDamaged = false;
                return true;
            }
            session.NextExpectedSeq = 1;
            if (last)
            {
                session.Phase = SdoBlockClientPhase.AwaitEnd;
            }
        }
        return true;
    }

    private void SendNextBlockDownloadSubBlock(SdoBlockClientSession session)
    {
        // Prepare all segments for this sub-block up front so they can be sent as a single
        // ordered Task chain (via SendOrderedControlFrames). We can NOT dispatch each
        // segment through the per-frame SendControlFrame helper because that Task.Runs
        // each send, and 127 concurrent Task.Runs would arrive at the peer in unspecified
        // order — the block-transfer receiver enforces monotonically increasing seqnos, so
        // any reordering aborts the whole transfer with SdoAbortCode.General.
        var frames = new List<(uint CobId, byte[] Payload)>(session.NegotiatedBlockSize);
        uint cobId = CanOpenCobId.SdoRx(session.ServerNodeId);
        // A fresh sub-block (seqno restarts at 1) anchors the rewind offset; a resumed
        // sub-block (after a partial ACK) keeps the original anchor and seqno numbering.
        if (session.ResumeSeqno == 1)
            session.SubBlockStartOffset = session.Offset;
        var seqno = session.ResumeSeqno;
        while (seqno <= session.NegotiatedBlockSize)
        {
            int remaining = session.Payload!.Length - session.Offset;
            if (remaining <= 0) break;

            int chunk = Math.Min(7, remaining);
            var segData = new byte[chunk];
            Buffer.BlockCopy(session.Payload, session.Offset, segData, 0, chunk);
            session.Offset += chunk;

            bool isLastOverall = session.Offset >= session.Payload.Length;
            var frame = SdoBlockFrames.BuildSegment(seqno, isLastSegment: isLastOverall, segData);
            frames.Add((cobId, frame));
            session.SubBlockLastSeqno = seqno;
            seqno++;

            if (isLastOverall)
            {
                session.LastSegmentUnusedBytes = (byte)(7 - chunk);
                break;
            }
        }
        session.ResumeSeqno = seqno;
        session.Phase = SdoBlockClientPhase.AwaitSubBlockAck;
        _ = SendOrderedControlFrames(frames.ToArray());
    }

    private void SendBlockDownloadEnd(SdoBlockClientSession session)
    {
        ushort crc = 0;
        if (session.CrcActive)
        {
            var full = new byte[session.Payload!.Length];
            Buffer.BlockCopy(session.Payload, 0, full, 0, full.Length);
            crc = SdoBlockFrames.ComputeCrc16Xmodem(full);
        }
        var end = SdoBlockFrames.BuildEnd(SdoBlockFrames.CcsBlockDownloadEndBase,
            session.LastSegmentUnusedBytes, crc);
        session.Phase = SdoBlockClientPhase.AwaitEndResponse;
        _ = SendControlFrame(CanOpenCobId.SdoRx(session.ServerNodeId), end);
    }

    // =========================================================================================
    // Server — block download (peer sends a payload to our OD).
    // =========================================================================================
    /// <summary>
    /// Called from <c>HandleIncoming</c> for a frame on our SDO Rx COB-ID *before* the classic
    /// <c>HandleSdoServerRequest</c>. Returns true if this handler consumed the frame — that
    /// is, either it was a block-transfer initiate for us, or a follow-up frame for an
    /// already-open block-server session.
    /// </summary>
    private bool HandleSdoServerRequestBlock(byte[] data)
    {
        // CiA 301: SDO (including block transfer) is not available in Stopped / Initializing.
        // Mirror HandleSdoServerRequest so block initiates and in-flight segment handling are
        // dropped the same way as classic SDO.
        if (_state is NmtState.Stopped or NmtState.Initializing)
            return false;

        if (data.Length == 0) return false;

        // Pad short DLC frames back to 8 bytes for parsing.
        if (data.Length < 8)
        {
            var padded = new byte[8];
            Buffer.BlockCopy(data, 0, padded, 0, data.Length);
            data = padded;
        }
        byte cs = data[0];

        // An abort during any active block-server phase tears the block session down and lets
        // the caller (nothing more to do at the classical layer since the peer is aborting our
        // OWN transfer). Handled here explicitly because in ReceivingSegments the segment
        // stream and cs=0x80 (abort) are otherwise indistinguishable without the "seq=0 is
        // invalid" hint.
        if (_sdoBlockServer is not null && cs == SdoFrames.CsAbort)
        {
            var stale = _sdoBlockServer;
            _sdoBlockServer = null;
            stale.Deadline?.Dispose();
            return true;
        }

        // A block-server session in its segment-receiving phase owns every frame on our SDO Rx
        // (#39). The phase says what to expect: byte 0 is (c << 7) | seqno (CiA 301 §7.2.4.3.10,
        // Figure 28), not a command specifier, and a value that happens to spell an initiate —
        // 0x21, 0x23, 0x40, 0xC2, ... — is an ordinary sequence number in that position. The
        // previous code guessed "initiate" from that byte pattern and let the classic server
        // supersede the running transfer, so a reordered or stray segment killed it. The one
        // frame that is not a segment here is the abort, taken above, and it stays
        // distinguishable without any guessing: its byte 0x80 decodes to seqno 0, which
        // §7.2.4.3.10 rules out ("0 < seqno < 128"). Seqno 0 with c = 0, or a seqno past the
        // blksize we announced, is a protocol error and is aborted inside the handler. A peer
        // that wants to start something else while a block download is open says so with an
        // abort first.
        if (_sdoBlockServer is { Phase: SdoBlockServerPhase.ReceivingSegments } rx)
        {
            HandleBlockDownloadServerSegment(rx, data, cs);
            return true;
        }
        if (_sdoBlockServer is { Phase: SdoBlockServerPhase.AwaitEnd } awaitingEnd
            && (cs & 0xE3) == CcsBlockDownloadEndMask)
        {
            HandleBlockDownloadServerEnd(awaitingEnd, data, cs);
            return true;
        }

        // NMT / block-init: check for a block-transfer initiate (0xC0/0xC4/0xC6 for download,
        // 0xA0/0xA4 for upload). Abort (0x80) is left to the classical server; the block
        // server section only intercepts its own control frames.
        // Block download initiate: ccs=6, cs=0. bits 5..7 = 110, bit 0 = 0.
        if ((cs & 0xE1) == CcsBlockDownloadInitMask)
        {
            HandleBlockDownloadServerInit(data, cs);
            return true;
        }
        // Block upload initiate: ccs=5, cs=0. bits 5..7 = 101, bits 0..1 = 00.
        if ((cs & 0xE3) == CcsBlockUploadInitMask)
        {
            HandleBlockUploadServerInit(data, cs);
            return true;
        }
        // Block upload start (0xA3), sub-block ack (0xA2), end response (0xA1).
        if (cs == SdoBlockFrames.CcsBlockUploadStart && _sdoBlockServer is { InDownload: false } up)
        {
            HandleBlockUploadServerStart(up);
            return true;
        }
        if (cs == SdoBlockFrames.CcsBlockUploadSubBlockAck
            && _sdoBlockServer is { InDownload: false, Phase: SdoBlockServerPhase.AwaitSubBlockAck } upAck)
        {
            HandleBlockUploadServerSubBlockAck(upAck, data);
            return true;
        }
        if (cs == SdoBlockFrames.CcsBlockUploadEndResponse
            && _sdoBlockServer is { InDownload: false, Phase: SdoBlockServerPhase.AwaitEndResponse } upEndResp)
        {
            _sdoBlockServer = null;
            upEndResp.Deadline?.Dispose();
            return true;
        }
        return false;
    }

    private void HandleBlockDownloadServerInit(byte[] data, byte cs)
    {
        AbortSupersededBlockServerSession();
        AbortSupersededServerSession();

        var (index, subindex) = SdoFrames.ReadIndex(data);
        _od.TryGet(index, subindex, out var entry);
        if (entry is null)
        {
            _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
                SdoFrames.BuildAbort(index, subindex, (uint)SdoAbortCode.ObjectDoesNotExist));
            return;
        }
        if ((entry.Access & OdAccess.WriteOnly) == 0)
        {
            _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
                SdoFrames.BuildAbort(index, subindex, (uint)SdoAbortCode.AttemptWriteReadOnly));
            return;
        }

        bool sizeIndicated = (cs & 0x02) != 0;
        uint declaredLen = 0;
        if (sizeIndicated)
        {
            declaredLen = (uint)(data[4] | (data[5] << 8) | (data[6] << 16) | (data[7] << 24));
            if (declaredLen > (uint)_options.MaxSdoTransferBytes)
            {
                _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
                    SdoFrames.BuildAbort(index, subindex, (uint)SdoAbortCode.OutOfMemory));
                return;
            }
        }

        bool crcActive = _options.SdoBlockCrcSupported && SdoBlockFrames.ReadCrcSupportedBit(cs);
        byte blkSize = _options.SdoBlockSize;

        int bufSize = sizeIndicated ? (int)declaredLen : 128;
        var session = new SdoBlockServerSession(
            inDownload: true,
            index, subindex,
            buffer: new byte[bufSize],
            offset: 0)
        {
            CrcActive = crcActive,
            NegotiatedBlockSize = blkSize,
            NextExpectedSeq = 1,
            DeclaredTotalSize = declaredLen,
            SizeIndicated = sizeIndicated,
            Phase = SdoBlockServerPhase.ReceivingSegments,
        };
        _sdoBlockServer = session;
        // Server-side sessions idle against SdoServerTimeout, the value the options document
        // for exactly this guard; SdoTimeout is the client's request timer (#17).
        session.Deadline = _deadlines.Arm(_options.SdoServerTimeout, OnSdoBlockServerTimeout);

        // Advertise local CRC capability (CiA 301 "sc" bit); CRC stays inactive for this
        // transfer unless both endpoints set their bits (captured in crcActive above).
        var resp = SdoBlockFrames.BuildBlockDownloadInitResponse(index, subindex,
            serverCrcSupported: _options.SdoBlockCrcSupported, blockSize: blkSize);
        _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId), resp);
    }

    private void HandleBlockDownloadServerSegment(SdoBlockServerSession session, byte[] data, byte cs)
    {
        byte seq = (byte)(cs & 0x7F);
        bool last = (cs & 0x80) != 0;

        // CiA 301 §7.2.4.3.10: "seqno: sequence number of segment 0 < seqno < 128", numbered
        // "starting by 1, which is increased for each segment by 1 up to blksize" (§7.2.4.2.10).
        // Zero — the abort's byte pattern, so never a segment — or a value past the blksize we
        // announced cannot belong to this sub-block: Table 22, 0504 0003h.
        if (seq == 0 || seq > session.NegotiatedBlockSize)
        {
            AbortBlockServer(session, SdoAbortCode.InvalidSequenceNumber);
            return;
        }

        // The peer is still talking, whatever it sent: the idle deadline restarts.
        RearmBlockServer(session);

        if (!session.SubBlockDamaged && seq == session.NextExpectedSeq)
        {
            // MaxSdoTransferBytes caps the data, and how much of the last segment is data is
            // unknown until the end frame's "n" says how many of its seven bytes are unused
            // (CiA 301 §7.2.4.3.11). So a segment is refused only once no byte of it could
            // still be within the cap — the cap is already full — and the buffer may run up to
            // six bytes past it until HandleBlockDownloadServerEnd checks the trimmed length.
            // Refusing whenever the whole seven-byte window would not fit aborted a transfer of
            // exactly MaxSdoTransferBytes at its last segment (#59).
            if (session.Offset >= _options.MaxSdoTransferBytes)
            {
                AbortBlockServer(session, SdoAbortCode.OutOfMemory);
                return;
            }
            // Ensure room for 7 bytes; grow if declared size was under-specified or unbounded.
            if (session.Offset + 7 > session.Buffer.Length)
            {
                var grown = new byte[session.Offset + 7];
                Buffer.BlockCopy(session.Buffer, 0, grown, 0, session.Offset);
                session.Buffer = grown;
            }
            Buffer.BlockCopy(data, 1, session.Buffer, session.Offset, 7);
            session.Offset += 7;
            session.NextExpectedSeq = (byte)(seq + 1);
        }
        else
        {
            // A lost or reordered segment. §7.2.4.3.10 answers a sub-block with one confirm whose
            // "ackseq: sequence number of last segment that was received successfully during the
            // last block download" tells the client where to resume; nothing in the protocol
            // answers individual segments. So the rest of this sub-block is ignored and the one
            // confirm at its end carries the news (#39). Before this, every further segment of
            // the sub-block drew its own confirm — up to 126 control frames for one lost frame,
            // on a bus that was already dropping frames.
            session.SubBlockDamaged = true;
        }

        // A sub-block ends with the segment numbered blksize or with c = 1 (§7.2.4.2.10), and
        // the end is recognised on the numbering the sender used, not on what was accepted, so
        // a damaged sub-block ends where the sender ends it.
        if (last || seq >= session.NegotiatedBlockSize)
        {
            var ack = SdoBlockFrames.BuildSubBlockAck(SdoBlockFrames.ScsBlockDownloadSubBlockAck,
                lastAckedSeq: (byte)(session.NextExpectedSeq - 1),
                nextBlockSize: session.NegotiatedBlockSize);
            _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId), ack);
            if (session.SubBlockDamaged)
            {
                // Partial confirm: the client resumes at ackseq + 1 with the original numbering
                // (this package's own client does, see SdoBlockClientSession.ResumeSeqno), so
                // NextExpectedSeq stays where the gap was and the sub-block continues.
                session.SubBlockDamaged = false;
                return;
            }
            session.NextExpectedSeq = 1;
            if (last) session.Phase = SdoBlockServerPhase.AwaitEnd;
        }
    }

    /// <summary>Aborts the open block-server session on the wire and drops it locally.</summary>
    private void AbortBlockServer(SdoBlockServerSession session, SdoAbortCode code)
    {
        _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
            SdoFrames.BuildAbort(session.Index, session.Subindex, (uint)code));
        _sdoBlockServer = null;
        session.Deadline?.Dispose();
    }

    private void HandleBlockDownloadServerEnd(SdoBlockServerSession session, byte[] data, byte cs)
    {
        byte n = SdoBlockFrames.ReadEndUnusedBytes(cs);
        ushort peerCrc = SdoBlockFrames.ReadEndCrc(data);
        if (n > 0)
        {
            if (session.Offset < n)
            {
                _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
                    SdoFrames.BuildAbort(session.Index, session.Subindex, (uint)SdoAbortCode.General));
                _sdoBlockServer = null;
                session.Deadline?.Dispose();
                return;
            }
            session.Offset -= n;
        }
        if (session.SizeIndicated && session.Offset != session.DeclaredTotalSize)
        {
            var reason = session.Offset > session.DeclaredTotalSize
                ? SdoAbortCode.LengthTooHigh : SdoAbortCode.LengthTooLow;
            _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
                SdoFrames.BuildAbort(session.Index, session.Subindex, (uint)reason));
            _sdoBlockServer = null;
            session.Deadline?.Dispose();
            return;
        }
        // The segment handler lets the last segment's window run past the cap because it cannot
        // know n yet (#59); with n applied, the data itself must be within the cap. A declared
        // size was capped at the initiate, so this only bites for an unbounded transfer.
        if (session.Offset > _options.MaxSdoTransferBytes)
        {
            AbortBlockServer(session, SdoAbortCode.OutOfMemory);
            return;
        }

        var final = new byte[session.Offset];
        Buffer.BlockCopy(session.Buffer, 0, final, 0, session.Offset);

        if (session.CrcActive)
        {
            var localCrc = SdoBlockFrames.ComputeCrc16Xmodem(final);
            if (localCrc != peerCrc)
            {
                _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
                    SdoFrames.BuildAbort(session.Index, session.Subindex, (uint)SdoAbortCode.CrcError));
                _sdoBlockServer = null;
                session.Deadline?.Dispose();
                return;
            }
        }

        if (!_od.TryWriteRaw(session.Index, session.Subindex, final, out var abort))
        {
            _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
                SdoFrames.BuildAbort(session.Index, session.Subindex, (uint)(abort ?? SdoAbortCode.General)));
            _sdoBlockServer = null;
            session.Deadline?.Dispose();
            return;
        }

        _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
            SdoBlockFrames.BuildEndResponse(SdoBlockFrames.ScsBlockDownloadEndResponse));
        _sdoBlockServer = null;
        session.Deadline?.Dispose();
    }

    // =========================================================================================
    // Server — block upload (peer requests our OD value, we stream it out).
    // =========================================================================================
    private void HandleBlockUploadServerInit(byte[] data, byte cs)
    {
        AbortSupersededBlockServerSession();
        AbortSupersededServerSession();

        var (index, subindex) = SdoFrames.ReadIndex(data);
        _od.TryGet(index, subindex, out var entry);
        if (entry is null)
        {
            _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
                SdoFrames.BuildAbort(index, subindex, (uint)SdoAbortCode.ObjectDoesNotExist));
            return;
        }
        if ((entry.Access & OdAccess.ReadOnly) == 0)
        {
            _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
                SdoFrames.BuildAbort(index, subindex, (uint)SdoAbortCode.AttemptReadWriteOnly));
            return;
        }

        if (!_od.TryReadRaw(index, subindex, out var value))
        {
            _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
                SdoFrames.BuildAbort(index, subindex, (uint)SdoAbortCode.ObjectDoesNotExist));
            return;
        }

        // The peer's blksize is byte 4. CiA 301 §7.2.4.3.13 (Figure 31): "blksize: Number of
        // segments per block with 0 < blksize < 128." A value outside that range is not a
        // request to be repaired with a guess — 0 used to be replaced by our own default and
        // anything above 127 clamped — it is an invalid block size, Table 22 0504 0002h (#59).
        byte peerBlkSize = data[4];
        if (peerBlkSize is < 1 or > 127)
        {
            _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
                SdoFrames.BuildAbort(index, subindex, (uint)SdoAbortCode.InvalidBlockSize));
            return;
        }

        bool crcActive = _options.SdoBlockCrcSupported && SdoBlockFrames.ReadCrcSupportedBit(cs);

        var session = new SdoBlockServerSession(
            inDownload: false,
            index, subindex,
            buffer: value,
            offset: 0)
        {
            CrcActive = crcActive,
            NegotiatedBlockSize = peerBlkSize,
            DeclaredTotalSize = (uint)value.Length,
            SizeIndicated = true,
            Phase = SdoBlockServerPhase.AwaitStart,
        };
        _sdoBlockServer = session;
        // Server-side sessions idle against SdoServerTimeout (see HandleBlockDownloadServerInit).
        session.Deadline = _deadlines.Arm(_options.SdoServerTimeout, OnSdoBlockServerTimeout);

        // Advertise local CRC capability (CiA 301 "sc" bit); CRC stays inactive for this
        // transfer unless both endpoints set their bits (captured in crcActive above).
        var resp = SdoBlockFrames.BuildBlockUploadInitResponse(index, subindex,
            serverCrcSupported: _options.SdoBlockCrcSupported, sizeIndicated: true, totalSize: (uint)value.Length);
        _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId), resp);
    }

    private void HandleBlockUploadServerStart(SdoBlockServerSession session)
    {
        // The peer spoke: the idle deadline restarts here and on every sub-block ACK (#17).
        RearmBlockServer(session);
        session.Phase = SdoBlockServerPhase.SendingSegments;
        SendNextBlockUploadSubBlock(session);
    }

    private void HandleBlockUploadServerSubBlockAck(SdoBlockServerSession session, byte[] data)
    {
        RearmBlockServer(session);
        var (ackseq, nextBlkSize) = SdoBlockFrames.ReadSubBlockAck(data);
        // ackseq counts cumulatively from the start of the current sub-block
        // (CiA 301 §7.2.4.3.15). More than sent is a protocol violation; less asks for
        // retransmission from ackseq + 1 with the original seqnos.
        if (ackseq > session.SubBlockLastSeqno)
        {
            _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
                SdoFrames.BuildAbort(session.Index, session.Subindex, (uint)SdoAbortCode.General));
            _sdoBlockServer = null;
            session.Deadline?.Dispose();
            return;
        }
        if (nextBlkSize is < 1 or > 127)
        {
            _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
                SdoFrames.BuildAbort(session.Index, session.Subindex, (uint)SdoAbortCode.InvalidBlockSize));
            _sdoBlockServer = null;
            session.Deadline?.Dispose();
            return;
        }
        if (ackseq < session.SubBlockLastSeqno)
        {
            // Partial ACK: rewind to the first unconfirmed segment and retransmit from there
            // with the original sub-block seqnos (CiA 301 §7.2.4.3.15), bounded by
            // CanOpenNodeOptions.SdoBlockMaxRetransmissions (0 restores the old abort behavior).
            // The advertised blksize is for the following sub-block, not this retransmission
            // (#134, see the download client).
            if (++session.Retransmissions > _options.SdoBlockMaxRetransmissions)
            {
                _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
                    SdoFrames.BuildAbort(session.Index, session.Subindex, (uint)SdoAbortCode.General));
                _sdoBlockServer = null;
                session.Deadline?.Dispose();
                return;
            }
            // Confirmed segments are always full 7-byte chunks (a short final segment can
            // only sit at the tail, which is unconfirmed in a partial ACK).
            session.Offset = session.SubBlockStartOffset + ackseq * 7;
            session.ResumeSeqno = (byte)(ackseq + 1);
            session.Phase = SdoBlockServerPhase.SendingSegments;
            SendNextBlockUploadSubBlock(session);
            return;
        }

        // Full confirmation of the current sub-block: the next one restarts at seqno 1 and is
        // the first to use the advertised size.
        session.NegotiatedBlockSize = nextBlkSize;
        session.ResumeSeqno = 1;
        if (session.Offset >= session.Buffer.Length)
        {
            SendBlockUploadEnd(session);
            return;
        }
        session.Phase = SdoBlockServerPhase.SendingSegments;
        SendNextBlockUploadSubBlock(session);
    }

    private void SendNextBlockUploadSubBlock(SdoBlockServerSession session)
    {
        // Same ordering caveat as SendNextBlockDownloadSubBlock: send the whole sub-block
        // through SendOrderedControlFrames so seqnos arrive at the peer in order.
        var frames = new List<(uint CobId, byte[] Payload)>(session.NegotiatedBlockSize);
        uint cobId = CanOpenCobId.SdoTx(_nodeId);
        // A fresh sub-block (seqno restarts at 1) anchors the rewind offset; a resumed
        // sub-block (after a partial ACK) keeps the original anchor and seqno numbering.
        if (session.ResumeSeqno == 1)
            session.SubBlockStartOffset = session.Offset;
        var seqno = session.ResumeSeqno;
        while (seqno <= session.NegotiatedBlockSize)
        {
            int remaining = session.Buffer.Length - session.Offset;
            if (remaining <= 0) break;
            int chunk = Math.Min(7, remaining);
            var segData = new byte[chunk];
            Buffer.BlockCopy(session.Buffer, session.Offset, segData, 0, chunk);
            session.Offset += chunk;
            bool isLastOverall = session.Offset >= session.Buffer.Length;
            var frame = SdoBlockFrames.BuildSegment(seqno, isLastSegment: isLastOverall, segData);
            frames.Add((cobId, frame));
            session.SubBlockLastSeqno = seqno;
            seqno++;
            if (isLastOverall)
            {
                session.LastSegmentUnusedBytes = (byte)(7 - chunk);
                break;
            }
        }
        session.ResumeSeqno = seqno;
        session.Phase = SdoBlockServerPhase.AwaitSubBlockAck;
        _ = SendOrderedControlFrames(frames.ToArray());
    }

    private void SendBlockUploadEnd(SdoBlockServerSession session)
    {
        ushort crc = 0;
        if (session.CrcActive)
        {
            var full = new byte[session.Buffer.Length];
            Buffer.BlockCopy(session.Buffer, 0, full, 0, full.Length);
            crc = SdoBlockFrames.ComputeCrc16Xmodem(full);
        }
        var end = SdoBlockFrames.BuildEnd(SdoBlockFrames.ScsBlockUploadEndBase,
            session.LastSegmentUnusedBytes, crc);
        session.Phase = SdoBlockServerPhase.AwaitEndResponse;
        _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId), end);
    }

    private void OnSdoBlockServerTimeout()
    {
        var s = _sdoBlockServer;
        if (s is null) return;
        _sdoBlockServer = null;
        s.Deadline?.Dispose();
        _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
            SdoFrames.BuildAbort(s.Index, s.Subindex, (uint)SdoAbortCode.SdoProtocolTimedOut));
    }

    /// <summary>
    /// Re-arms the block server's idle deadline after any frame from the peer, so it measures
    /// how long the peer has been silent rather than how long the transfer has been running.
    /// Called on the download path per accepted segment and on the upload path per "start" and
    /// per sub-block ACK; before #17 the upload path never re-armed, so a transfer longer than
    /// the initial arm timed out while perfectly healthy (about 50 KB at 1 Mbit/s with the
    /// 1 s value that was armed then). Uses <see cref="CanOpenNodeOptions.SdoServerTimeout"/>,
    /// the value documented for this guard.
    /// </summary>
    private void RearmBlockServer(SdoBlockServerSession session)
    {
        var deadline = session.Deadline;
        if (deadline is null || deadline.IsExpired || deadline.IsCancelled
            || !deadline.Rearm(_options.SdoServerTimeout))
        {
            deadline?.Dispose();
            session.Deadline = _deadlines.Arm(_options.SdoServerTimeout, OnSdoBlockServerTimeout);
        }
    }

    private void AbortSupersededBlockServerSession()
    {
        var stale = _sdoBlockServer;
        if (stale is null) return;
        stale.Deadline?.Dispose();
        _sdoBlockServer = null;
        _ = SendControlFrame(CanOpenCobId.SdoTx(_nodeId),
            SdoFrames.BuildAbort(stale.Index, stale.Subindex, (uint)SdoAbortCode.General));
    }

    // =========================================================================================
    // Command-specifier masks used by phase dispatch.
    // =========================================================================================
    // Block download initiate mask (ccs=6, cs=0): bits 5..7 = 110, bit 0 = 0.
    private const byte CcsBlockDownloadInitMask = 0xC0;

    // Block upload initiate mask (ccs=5, cs=00): bits 5..7 = 101, bits 0..1 = 00.
    private const byte CcsBlockUploadInitMask = 0xA0;

    // Block download end mask (ccs=6, cs=1): bits 5..7 = 110, bits 0..1 = 01.
    private const byte CcsBlockDownloadEndMask = 0xC1;

    // Server-side block-download initiate response mask (scs=5, ss=0): bits 5..7 = 101, bits 0..1 = 00.
    private const byte ScsInitResponseMaskDownload = 0xA0;

    // Server-side block-upload initiate response mask (scs=6, ss=0): bits 5..7 = 110, bits 0..1 = 00.
    private const byte ScsInitResponseMaskUpload = 0xC0;

    // Server-side block-upload end mask (scs=6, ss=1): bits 5..7 = 110, bits 0..1 = 01.
    private const byte ScsBlockUploadEndMask = 0xC1;

    // Server-side block-download sub-block ack (scs=5, ss=2): 0xA2.
    private const byte ScsBlockDownloadSubBlockAck = 0xA2;

    // Server-side block-download end response (scs=5, ss=1): 0xA1.
    private const byte ScsBlockDownloadEndResponse = 0xA1;

    // =========================================================================================
    // Session state objects (private nested).
    // =========================================================================================

    private enum SdoBlockClientPhase
    {
        AwaitInitResponse = 0,
        SendingSegments = 1,
        AwaitSubBlockAck = 2,
        AwaitEndResponse = 3,
        ReceivingSegments = 4,
        AwaitEnd = 5,
    }

    private enum SdoBlockServerPhase
    {
        AwaitInit = 0,
        ReceivingSegments = 1,
        AwaitEnd = 2,
        SendingSegments = 3,
        AwaitSubBlockAck = 4,
        AwaitEndResponse = 5,
        AwaitStart = 6,
    }

    private sealed class SdoBlockClientSession
    {
        public SdoBlockClientSession(byte serverNodeId, ushort index, byte subindex,
            bool isDownload, byte[]? payload, TaskCompletionSource<byte[]> tcs, bool localCrcSupported)
        {
            ServerNodeId = serverNodeId;
            Index = index;
            Subindex = subindex;
            IsDownload = isDownload;
            Payload = payload;
            Tcs = tcs;
            LocalCrcSupported = localCrcSupported;
        }

        public byte ServerNodeId { get; }
        public ushort Index { get; }
        public byte Subindex { get; }
        public bool IsDownload { get; }
        public byte[]? Payload { get; set; }
        public TaskCompletionSource<byte[]> Tcs { get; }
        public bool LocalCrcSupported { get; }
        public bool CrcActive { get; set; }
        public SdoBlockClientPhase Phase { get; set; } = SdoBlockClientPhase.AwaitInitResponse;

        // Download progress.
        public int Offset;
        public byte NegotiatedBlockSize;
        public byte LastSegmentUnusedBytes;

        // Retransmission state (CiA 301 §7.2.4.3.15): the sub-block sequence numbering is
        // cumulative per sub-block and must survive a partial-ACK rewind — resending with a
        // fresh seqno=1 would make the peer NACK forever.
        public int SubBlockStartOffset;      // payload offset where the current sub-block began
        public byte ResumeSeqno = 1;         // next seqno (1-based) to use within the sub-block
        public byte SubBlockLastSeqno;       // highest seqno sent so far in the current sub-block
        public int Retransmissions;

        // Upload progress.
        public byte LocalBlockSize = 127;
        public byte NextExpectedSeq = 1;
        public uint DeclaredTotalSize;
        // A segment of the current sub-block was lost or reordered: the rest of the sub-block
        // is ignored and its one confirm carries the last good seqno (#39, CiA 301 §7.2.4.3.14).
        public bool SubBlockDamaged;

        public IDeadline? Deadline;
    }

    private sealed class SdoBlockServerSession
    {
        public SdoBlockServerSession(bool inDownload, ushort index, byte subindex, byte[] buffer, int offset)
        {
            InDownload = inDownload;
            Index = index;
            Subindex = subindex;
            Buffer = buffer;
            Offset = offset;
        }

        public bool InDownload { get; }
        public ushort Index { get; }
        public byte Subindex { get; }
        public byte[] Buffer { get; set; }
        public int Offset { get; set; }
        public SdoBlockServerPhase Phase { get; set; }
        public byte NegotiatedBlockSize { get; set; }
        public byte NextExpectedSeq { get; set; }
        // A segment of the current sub-block was lost or reordered: the rest of the sub-block
        // is ignored and its one confirm carries the last good seqno (#39, CiA 301 §7.2.4.3.10).
        public bool SubBlockDamaged { get; set; }
        public byte LastSegmentUnusedBytes { get; set; }
        public bool CrcActive { get; set; }
        public uint DeclaredTotalSize { get; set; }
        public bool SizeIndicated { get; set; }
        public IDeadline? Deadline { get; set; }

        // Retransmission state (CiA 301 §7.2.4.3.15) for the upload-server send path: the
        // sub-block sequence numbering is cumulative per sub-block and must survive a
        // partial-ACK rewind (the download-server receive path counts via NextExpectedSeq /
        // SegmentsInSubBlock above instead).
        public int SubBlockStartOffset { get; set; }
        public byte ResumeSeqno { get; set; } = 1;
        public byte SubBlockLastSeqno { get; set; }
        public int Retransmissions { get; set; }
    }
}
