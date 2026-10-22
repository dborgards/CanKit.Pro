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
}
