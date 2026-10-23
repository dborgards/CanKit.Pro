using System;
using AwesomeAssertions;
using CanKit.Pro.Uds;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.Uds;

/// <summary>
/// The P2* noted with a response window (Codex on #382): a window noted again while it is open
/// keeps the longer one, a window that had closed before the next send passes none on.
/// </summary>
public class SuppressedResponseWindowsTests
{
    private const byte Sid = 0x3E;
    private const long Hz = 1000; // one tick per millisecond
    private static readonly TimeSpan P2 = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(1500);

    [Fact]
    public void A_Window_Noted_Again_While_Open_Keeps_The_Longer_P2Star()
    {
        var windows = new SuppressedResponseWindows();
        windows.Note(Sid, 1000, P2, Hz, Long);   // open until 1300
        windows.Note(Sid, 1300, P2, Hz, Short);  // at its deadline: still open

        windows.TryGetPendingExtension(Sid, out var extension).Should().BeTrue();
        extension.Should().Be(Long);
        windows.TryGetDeadline(Sid, out var until).Should().BeTrue();
        until.Should().Be(1600);
    }

    [Fact]
    public void A_Window_That_Had_Closed_Passes_No_P2Star_On()
    {
        var windows = new SuppressedResponseWindows();
        windows.Note(Sid, 1000, P2, Hz, Long);   // open until 1300
        windows.Note(Sid, 1301, P2, Hz, Short);  // after it closed

        windows.TryGetPendingExtension(Sid, out var extension).Should().BeTrue();
        extension.Should().Be(Short);
        windows.TryGetDeadline(Sid, out var until).Should().BeTrue();
        until.Should().Be(1601);
    }

    // #354 (U-5): the 0x78s that move a window out are bounded per note -- per request that went
    // out -- so the next request's own 0x78s are counted afresh, its window still open or not.
    [Fact]
    public void A_New_Note_Counts_The_Pending_Answers_That_Move_The_Window_Afresh()
    {
        var windows = new SuppressedResponseWindows(maxExtensions: 1);
        windows.Note(Sid, 1000, P2, Hz);          // open until 1300
        windows.ExtendIfOpenAt(Sid, 1100, 2600);  // the one 0x78 allowed
        windows.ExtendIfOpenAt(Sid, 1200, 2700);  // one too many
        windows.TryGetDeadline(Sid, out var until).Should().BeTrue();
        until.Should().Be(2600);

        windows.Note(Sid, 2000, P2, Hz);          // the next request, inside that window
        windows.ExtendIfOpenAt(Sid, 2100, 3600);  // its own first 0x78
        windows.TryGetDeadline(Sid, out until).Should().BeTrue();
        until.Should().Be(3600);
    }
}
