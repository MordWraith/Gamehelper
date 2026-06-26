namespace GameOffsets.Objects.States
{
    using System;
    using System.Runtime.InteropServices;

    // Ghidra function ref: search for "Abnormal disconnect: "
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct InGameStateOffset
    {
        [FieldOffset(0x290)] public IntPtr AreaInstanceData; // contains area level
        [FieldOffset(0x368)] public IntPtr WorldData; // contains area name
        [FieldOffset(0x2F0)] public IntPtr UiRootStructPtr; // UiRootStruct
        [FieldOffset(0x318)] public IntPtr GamepadUiRootStructPtr; // UiRootStruct for controller mode
        [FieldOffset(0x300)] public IntPtr MouseOverHostPtr; // 1st hop towards the entity under the cursor
    }

    // Pointer chain to the "MouseOver" entity (the TRUE entity currently under the cursor,
    // monsters included). Verified live in PoE2 v0.5.4:
    //   host = ReadPtr(InGameState + 0x300)
    //   sub  = ReadPtr(host        + 0x3F0)
    //   ent  = ReadPtr(sub         + 0xA8)   // 0 when nothing hovered
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct MouseOverHostStruct
    {
        [FieldOffset(0x3F0)] public IntPtr MouseOverSubPtr;
    }

    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct MouseOverSubStruct
    {
        [FieldOffset(0xA8)] public IntPtr EntityPtr; // 0 when nothing hovered
    }
    
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct UiRootStruct
    {
        [FieldOffset(0x340)] public IntPtr UiRootPtr; // contains self pointer
        [FieldOffset(0xBE0)] public IntPtr GameUiPtr; // contains self pointer
        [FieldOffset(0xBE8)] public IntPtr GameUiControllerPtr; // contains self pointer
    }
}
