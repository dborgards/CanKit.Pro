namespace CanKit.Pro.CANopen.Safety;

internal sealed partial class SrdoEngine
{
    public bool TryHandleFrame(uint cobId, byte[] data, bool isRtr) => false;
    public bool TrySendGfc() => false;
    private void ArmConsumer(SrdoRuntime rt) => SetInvalid(rt, SrdoInvalidReason.NotReceived);
    private void DisarmConsumer(SrdoRuntime rt) { }
}
