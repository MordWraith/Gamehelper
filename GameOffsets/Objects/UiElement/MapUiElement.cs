namespace GameOffsets.Objects.UiElement
{
    using System.Runtime.InteropServices;
    using Natives;

    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct MapUiElementOffset
    {
        [FieldOffset(0x000)] public UiElementBaseOffset UiElementBase;
        [FieldOffset(0x350)] public StdTuple2D<float> Shift;
        [FieldOffset(0x358)] public StdTuple2D<float> DefaultShift; // normally v2=(0, -20f) on the large map
        [FieldOffset(0x390)] public float Zoom;
    }
}
