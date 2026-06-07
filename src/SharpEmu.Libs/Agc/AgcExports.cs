// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using System.Buffers.Binary;
using System.Threading;

namespace SharpEmu.Libs.Agc;

public static class AgcExports
{
    private const uint ShaderFileHeader = 0x34333231;
    private const uint ShaderVersion = 0x18;
    private const uint ItNop = 0x10;
    private const uint ItIndexBufferSize = 0x13;
    private const uint ItIndexBase = 0x26;
    private const uint ItIndexType = 0x2A;
    private const uint ItDrawIndexOffset2 = 0x35;
    private const uint ItSetShReg = 0x76;
    private const uint RZero = 0x00;
    private const uint RDrawIndexAuto = 0x04;
    private const uint RDrawReset = 0x05;
    private const uint RWaitFlipDone = 0x06;
    private const uint RShRegsIndirect = 0x11;
    private const uint RCxRegsIndirect = 0x12;
    private const uint RUcRegsIndirect = 0x13;
    private const uint RFlip = 0x17;
    private const uint SpiShaderPgmLoPs = 0x8;
    private const uint SpiShaderPgmHiPs = 0x9;
    private const uint SpiShaderPgmLoEs = 0xC8;
    private const uint SpiShaderPgmHiEs = 0xC9;
    private const uint SpiPsInputCntl0 = 0x191;
    private const uint VgtPrimitiveType = 0x242;
    private const uint PsTextureUserDataRegister = 0xC;
    private const uint Gen5TextureFormatR8G8B8A8Unorm = 56;
    private const uint Gen5TextureType2D = 9;
    private const ulong VideoOutPixelFormatA8R8G8B8Srgb = 0x80000000;
    private const ulong VideoOutPixelFormatA8B8G8R8Srgb = 0x80002200;

    private const ulong ShaderUserDataOffset = 0x08;
    private const ulong ShaderCodeOffset = 0x10;
    private const ulong ShaderCxRegistersOffset = 0x18;
    private const ulong ShaderShRegistersOffset = 0x20;
    private const ulong ShaderSpecialsOffset = 0x28;
    private const ulong ShaderInputSemanticsOffset = 0x30;
    private const ulong ShaderOutputSemanticsOffset = 0x38;
    private const ulong ShaderNumInputSemanticsOffset = 0x50;
    private const ulong ShaderNumOutputSemanticsOffset = 0x56;
    private const ulong ShaderTypeOffset = 0x5A;
    private const ulong ShaderNumShRegistersOffset = 0x5C;
    private const ulong CommandBufferCursorUpOffset = 0x10;
    private const ulong CommandBufferCursorDownOffset = 0x18;
    private const ulong CommandBufferCallbackOffset = 0x20;
    private const ulong CommandBufferReservedDwOffset = 0x30;
    private const ulong ShaderSpecialGeCntlOffset = 0x00;
    private const ulong ShaderSpecialVgtShaderStagesEnOffset = 0x08;
    private const ulong ShaderSpecialVgtGsOutPrimTypeOffset = 0x20;
    private const ulong ShaderSpecialGeUserVgprEnOffset = 0x28;
    private const uint CbSetShRegisterRangeMarker = 0x6875000D;
    private static readonly object _softwarePresenterGate = new();
    private static readonly Dictionary<(ulong Source, ulong Destination), ulong> _softwarePresenterFingerprints = new();
    private static int _submitTraceCaptured;

    private readonly record struct TextureDescriptor(
        ulong Address,
        uint Width,
        uint Height,
        uint Format,
        uint TileMode,
        uint Type);

    [SysAbiExport(
        Nid = "f3dg2CSgRKY",
        ExportName = "sceAgcCreateShader",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CreateShader(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        var headerAddress = ctx[CpuRegister.Rsi];
        var codeAddress = ctx[CpuRegister.Rdx];
        if (destinationAddress == 0 || headerAddress == 0 || codeAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt32(ctx, headerAddress, out var fileHeader) ||
            !TryReadUInt32(ctx, headerAddress + sizeof(uint), out var version))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (fileHeader != ShaderFileHeader || version != ShaderVersion)
        {
            TraceCreateShader(destinationAddress, headerAddress, codeAddress, $"invalid-header file=0x{fileHeader:X8} version=0x{version:X8}");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!RelocatePointerField(ctx, headerAddress + ShaderCxRegistersOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderShRegistersOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderUserDataOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderSpecialsOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderInputSemanticsOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderOutputSemanticsOffset) ||
            !ctx.TryWriteUInt64(headerAddress + ShaderCodeOffset, codeAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryReadUInt64(ctx, headerAddress + ShaderUserDataOffset, out var userDataAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (userDataAddress != 0 &&
            (!RelocatePointerField(ctx, userDataAddress) ||
             !RelocatePointerField(ctx, userDataAddress + 0x08) ||
             !RelocatePointerField(ctx, userDataAddress + 0x10) ||
             !RelocatePointerField(ctx, userDataAddress + 0x18) ||
             !RelocatePointerField(ctx, userDataAddress + 0x20)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!PatchShaderProgramRegisters(ctx, headerAddress, codeAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryWriteUInt64(destinationAddress, headerAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceCreateShader(destinationAddress, headerAddress, codeAddress, "ok");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "vcmNN+AAXnY",
        ExportName = "sceAgcSetCxRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetCxRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "cx");

    [SysAbiExport(
        Nid = "Qrj4c+61z4A",
        ExportName = "sceAgcSetShRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetShRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "sh");

    [SysAbiExport(
        Nid = "6lNcCp+fxi4",
        ExportName = "sceAgcSetUcRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetUcRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "uc");

    [SysAbiExport(
        Nid = "d-6uF9sZDIU",
        ExportName = "sceAgcSetCxRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetCxRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "cx");

    [SysAbiExport(
        Nid = "z2duB-hHQSM",
        ExportName = "sceAgcSetShRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetShRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "sh");

    [SysAbiExport(
        Nid = "vRoArM9zaIk",
        ExportName = "sceAgcSetUcRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetUcRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "uc");

    [SysAbiExport(
        Nid = "D9sr1xGUriE",
        ExportName = "sceAgcCreatePrimState",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CreatePrimState(CpuContext ctx)
    {
        var cxRegistersAddress = ctx[CpuRegister.Rdi];
        var ucRegistersAddress = ctx[CpuRegister.Rsi];
        var hullShaderAddress = ctx[CpuRegister.Rdx];
        var geometryShaderAddress = ctx[CpuRegister.Rcx];
        var primitiveType = (uint)ctx[CpuRegister.R8];

        if (cxRegistersAddress == 0 || ucRegistersAddress == 0 || hullShaderAddress != 0 || geometryShaderAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadByte(ctx, geometryShaderAddress + ShaderTypeOffset, out var shaderType) || shaderType != 2 ||
            !TryReadUInt64(ctx, geometryShaderAddress + ShaderSpecialsOffset, out var specialsAddress) ||
            specialsAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!CopyShaderRegister(ctx, specialsAddress + ShaderSpecialVgtShaderStagesEnOffset, cxRegistersAddress) ||
            !CopyShaderRegister(ctx, specialsAddress + ShaderSpecialVgtGsOutPrimTypeOffset, cxRegistersAddress + 8) ||
            !CopyShaderRegister(ctx, specialsAddress + ShaderSpecialGeCntlOffset, ucRegistersAddress) ||
            !CopyShaderRegister(ctx, specialsAddress + ShaderSpecialGeUserVgprEnOffset, ucRegistersAddress + 8) ||
            !TryWriteUInt32(ctx, ucRegistersAddress + 16, VgtPrimitiveType) ||
            !TryWriteUInt32(ctx, ucRegistersAddress + 20, primitiveType))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.create_prim_state cx=0x{cxRegistersAddress:X16} uc=0x{ucRegistersAddress:X16} gs=0x{geometryShaderAddress:X16} prim=0x{primitiveType:X8}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "HV4j+E0MBHE",
        ExportName = "sceAgcCreateInterpolantMapping",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CreateInterpolantMapping(CpuContext ctx)
    {
        var registersAddress = ctx[CpuRegister.Rdi];
        var geometryShaderAddress = ctx[CpuRegister.Rsi];
        var pixelShaderAddress = ctx[CpuRegister.Rdx];

        if (registersAddress == 0 || geometryShaderAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt64(ctx, geometryShaderAddress + ShaderOutputSemanticsOffset, out var outputSemanticsAddress) ||
            !TryReadUInt32(ctx, geometryShaderAddress + ShaderNumOutputSemanticsOffset, out var outputSemanticsCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ulong inputSemanticsAddress = 0;
        if (pixelShaderAddress != 0 &&
            (!TryReadUInt64(ctx, pixelShaderAddress + ShaderInputSemanticsOffset, out inputSemanticsAddress) ||
             !TryReadUInt32(ctx, pixelShaderAddress + ShaderNumInputSemanticsOffset, out _)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        for (uint i = 0; i < 32; i++)
        {
            uint value = 0;
            if (i < outputSemanticsCount && outputSemanticsAddress != 0)
            {
                var flat = false;
                if (pixelShaderAddress != 0 && inputSemanticsAddress != 0 &&
                    TryReadUInt32(ctx, inputSemanticsAddress + (i * sizeof(uint)), out var inputSemantic))
                {
                    flat = ((inputSemantic >> 22) & 0x1) != 0;
                }

                value = i | (flat ? 0x400u : 0u);
            }

            var destination = registersAddress + (i * 8);
            if (!TryWriteUInt32(ctx, destination, SpiPsInputCntl0 + i) ||
                !TryWriteUInt32(ctx, destination + sizeof(uint), value))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        TraceAgc($"agc.create_interpolant_mapping regs=0x{registersAddress:X16} gs=0x{geometryShaderAddress:X16} ps=0x{pixelShaderAddress:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "V++UgBtQhn0",
        ExportName = "sceAgcGetDataPacketPayloadAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetDataPacketPayloadAddress(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        var commandAddress = ctx[CpuRegister.Rsi];
        var type = (int)ctx[CpuRegister.Rdx];
        if (outputAddress == 0 || commandAddress == 0 || type != 1)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryWriteUInt64(outputAddress, commandAddress + 8))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.get_packet_payload out=0x{outputAddress:X16} cmd=0x{commandAddress:X16} payload=0x{commandAddress + 8:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "n2fD4A+pb+g",
        ExportName = "sceAgcCbSetShRegisterRangeDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbSetShRegisterRangeDirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var offset = (uint)ctx[CpuRegister.Rsi];
        var valuesAddress = ctx[CpuRegister.Rdx];
        var valueCount = (uint)ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0 || offset == 0 || offset > 0x3FF || valueCount == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var markerAddress) ||
            !TryWriteUInt32(ctx, markerAddress, Pm4(2, ItNop, RZero)) ||
            !TryWriteUInt32(ctx, markerAddress + 4, CbSetShRegisterRangeMarker) ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, valueCount + 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(valueCount + 2, ItSetShReg, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, offset))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint i = 0; i < valueCount; i++)
        {
            var value = 0u;
            if (valuesAddress != 0 &&
                !TryReadUInt32(ctx, valuesAddress + (i * sizeof(uint)), out value))
            {
                return ReturnPointer(ctx, 0);
            }

            if (!TryWriteUInt32(ctx, commandAddress + 8 + (i * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        TraceAgc($"agc.cb_set_sh_range buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} offset=0x{offset:X8} count={valueCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "TRO721eVt4g",
        ExportName = "sceAgcDcbResetQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbResetQueue(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var op = (uint)ctx[CpuRegister.Rsi];
        var state = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || op != 0x3FF || state != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RDrawReset)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_reset_queue buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "ZvwO9euwYzc",
        ExportName = "sceAgcDcbSetCxRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetCxRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RCxRegsIndirect, "cx");

    [SysAbiExport(
        Nid = "-HOOCn0JY48",
        ExportName = "sceAgcDcbSetShRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetShRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RShRegsIndirect, "sh");

    [SysAbiExport(
        Nid = "hvUfkUIQcOE",
        ExportName = "sceAgcDcbSetUcRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetUcRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RUcRegsIndirect, "uc");

    [SysAbiExport(
        Nid = "GIIW2J37e70",
        ExportName = "sceAgcDcbSetIndexSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexSize(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexSize = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        if (commandBufferAddress == 0 || cachePolicy != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItIndexType, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexSize))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_index_size buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} size={indexSize}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "l4fM9K-Lyks",
        ExportName = "sceAgcDcbSetIndexBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexBuffer(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexBufferAddress = ctx[CpuRegister.Rsi];
        var indexCount = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(3, ItIndexBase, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)(indexBufferAddress & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(indexBufferAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, Pm4(2, ItIndexBufferSize, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 16, indexCount))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_index_buffer buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} addr=0x{indexBufferAddress:X16} count={indexCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "B+aG9DUnTKA",
        ExportName = "sceAgcDcbDrawIndexOffset",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexOffset(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexOffset = (uint)ctx[CpuRegister.Rsi];
        var indexCount = (uint)ctx[CpuRegister.Rdx];
        var flags = (uint)ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItDrawIndexOffset2, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexCount) ||
            !TryWriteUInt32(ctx, commandAddress + 8, indexOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 12, indexCount) ||
            !TryWriteUInt32(ctx, commandAddress + 16, flags & 0xE000_0001u))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_draw_index_offset buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} offset={indexOffset} count={indexCount} flags=0x{flags:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "MWiElSNE8j8",
        ExportName = "sceAgcDcbWaitUntilSafeForRendering",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbWaitUntilSafeForRendering(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var videoOutHandle = (uint)ctx[CpuRegister.Rsi];
        var displayBufferIndex = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 7, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(7, ItNop, RWaitFlipDone)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, videoOutHandle) ||
            !TryWriteUInt32(ctx, commandAddress + 8, displayBufferIndex) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_wait_safe buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} handle={videoOutHandle} index={displayBufferIndex}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "YUeqkyT7mEQ",
        ExportName = "sceAgcDcbSetFlip",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetFlip(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var videoOutHandle = (uint)ctx[CpuRegister.Rsi];
        var displayBufferIndex = (int)ctx[CpuRegister.Rdx];
        var flipMode = (uint)ctx[CpuRegister.Rcx];
        var flipArg = unchecked((ulong)ctx[CpuRegister.R8]);
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 6, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(6, ItNop, RFlip)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, videoOutHandle) ||
            !TryWriteUInt32(ctx, commandAddress + 8, unchecked((uint)displayBufferIndex)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, flipMode) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(flipArg & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, (uint)(flipArg >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_flip buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} handle={videoOutHandle} index={displayBufferIndex} mode={flipMode} arg=0x{flipArg:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "UglJIZjGssM",
        ExportName = "sceAgcDriverSubmitDcb",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitDcb(CpuContext ctx)
    {
        var packetAddress = ctx[CpuRegister.Rdi];
        if (packetAddress == 0 ||
            !TryReadUInt64(ctx, packetAddress, out var commandAddress) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var dwordCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        TraceAgc($"agc.driver_submit_dcb packet=0x{packetAddress:X16} addr=0x{commandAddress:X16} dwords={dwordCount}");
        var tracePackets =
            string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"), "1", StringComparison.Ordinal) &&
            Interlocked.CompareExchange(ref _submitTraceCaptured, 1, 0) == 0;
        ParseSubmittedDcb(ctx, commandAddress, dwordCount, tracePackets);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "h9z6+0hEydk",
        ExportName = "sceAgcSuspendPoint",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SuspendPoint(CpuContext ctx)
    {
        TraceAgc("agc.suspend_point");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static void ParseSubmittedDcb(CpuContext ctx, ulong commandAddress, uint dwordCount, bool tracePackets)
    {
        if (commandAddress == 0 || dwordCount == 0 || dwordCount > 1_000_000)
        {
            return;
        }

        TextureDescriptor? presenterTexture = null;
        var sawIndexedDraw = false;
        var offset = 0u;
        while (offset < dwordCount)
        {
            var currentAddress = commandAddress + ((ulong)offset * sizeof(uint));
            if (!TryReadUInt32(ctx, currentAddress, out var header))
            {
                return;
            }

            var length = Pm4Length(header);
            if (length == 0 || offset + length > dwordCount)
            {
                return;
            }

            var op = (header >> 8) & 0xFFu;
            var register = (header >> 2) & 0x3Fu;
            if (tracePackets)
            {
                TraceSubmittedPacket(ctx, currentAddress, offset, header, length, op, register);
            }

            if (op == ItSetShReg &&
                TryReadTextureDescriptor(ctx, currentAddress, length, out var texture))
            {
                presenterTexture = texture;
            }

            if (op == ItDrawIndexOffset2 &&
                length >= 5 &&
                TryReadUInt32(ctx, currentAddress + 4, out var indexCount) &&
                indexCount != 0)
            {
                sawIndexedDraw = true;
            }

            if (op == ItNop && register == RFlip && length >= 6)
            {
                if (!TryReadUInt32(ctx, currentAddress + 4, out var videoOutHandle) ||
                    !TryReadUInt32(ctx, currentAddress + 8, out var displayBufferIndexRaw) ||
                    !TryReadUInt32(ctx, currentAddress + 12, out var flipMode) ||
                    !TryReadUInt32(ctx, currentAddress + 16, out var flipArgLo) ||
                    !TryReadUInt32(ctx, currentAddress + 20, out var flipArgHi))
                {
                    return;
                }

                var flipArg = unchecked((long)(((ulong)flipArgHi << 32) | flipArgLo));
                var displayBufferIndex = unchecked((int)displayBufferIndexRaw);
                if (sawIndexedDraw && presenterTexture is { } sourceTexture)
                {
                    _ = TrySoftwarePresent(
                        ctx,
                        sourceTexture,
                        unchecked((int)videoOutHandle),
                        displayBufferIndex);
                }

                _ = VideoOutExports.SubmitFlipFromAgc(ctx, unchecked((int)videoOutHandle), displayBufferIndex, unchecked((int)flipMode), flipArg);
            }

            offset += length;
        }
    }

    private static bool TryReadTextureDescriptor(
        CpuContext ctx,
        ulong packetAddress,
        uint packetLength,
        out TextureDescriptor descriptor)
    {
        descriptor = default;
        if (packetLength < 10 ||
            !TryReadUInt32(ctx, packetAddress + 4, out var startRegister))
        {
            return false;
        }

        var valueCount = packetLength - 2;
        if (startRegister > PsTextureUserDataRegister ||
            startRegister + valueCount < PsTextureUserDataRegister + 8)
        {
            return false;
        }

        var descriptorAddress =
            packetAddress +
            8 +
            ((ulong)(PsTextureUserDataRegister - startRegister) * sizeof(uint));
        Span<uint> fields = stackalloc uint[4];
        for (var i = 0; i < fields.Length; i++)
        {
            if (!TryReadUInt32(ctx, descriptorAddress + ((ulong)i * sizeof(uint)), out fields[i]))
            {
                return false;
            }
        }

        var address = ((((ulong)fields[1] << 32) | fields[0]) & 0xFF_FFFF_FFFFUL) << 8;
        var width = (((fields[1] >> 30) & 0x3u) | ((fields[2] & 0xFFFu) << 2)) + 1;
        var height = ((fields[2] >> 14) & 0x3FFFu) + 1;
        var format = (fields[1] >> 20) & 0x1FFu;
        var tileMode = (fields[3] >> 20) & 0x1Fu;
        var type = (fields[3] >> 28) & 0xFu;
        if (address == 0 || width == 0 || height == 0)
        {
            return false;
        }

        descriptor = new TextureDescriptor(address, width, height, format, tileMode, type);
        return true;
    }

    private static bool TrySoftwarePresent(
        CpuContext ctx,
        TextureDescriptor source,
        int videoOutHandle,
        int displayBufferIndex)
    {
        if (source.Format != Gen5TextureFormatR8G8B8A8Unorm ||
            source.TileMode != 0 ||
            source.Type != Gen5TextureType2D ||
            source.Width > 8192 ||
            source.Height > 8192 ||
            !VideoOutExports.TryGetDisplayBufferInfo(videoOutHandle, displayBufferIndex, out var destination) ||
            destination.Address == 0 ||
            destination.Width == 0 ||
            destination.Height == 0 ||
            destination.Width > 8192 ||
            destination.Height > 8192 ||
            destination.TilingMode != 0 ||
            destination.PixelFormat is not (VideoOutPixelFormatA8R8G8B8Srgb or VideoOutPixelFormatA8B8G8R8Srgb))
        {
            return false;
        }

        var sourceByteCount = checked((ulong)source.Width * source.Height * 4);
        if (sourceByteCount > 256UL * 1024UL * 1024UL)
        {
            return false;
        }

        var sourceBytes = new byte[(int)sourceByteCount];
        if (!ctx.Memory.TryRead(source.Address, sourceBytes))
        {
            return false;
        }

        var fingerprint = ComputeFingerprint(sourceBytes);
        var fingerprintKey = (source.Address, destination.Address);
        lock (_softwarePresenterGate)
        {
            if (_softwarePresenterFingerprints.TryGetValue(fingerprintKey, out var previousFingerprint) &&
                previousFingerprint == fingerprint)
            {
                return true;
            }
        }

        var destinationPitch = destination.PitchInPixel == 0
            ? destination.Width
            : destination.PitchInPixel;
        if (destinationPitch < destination.Width)
        {
            return false;
        }

        var destinationRow = new byte[checked((int)destinationPitch * 4)];
        var rgbaDestination = destination.PixelFormat == VideoOutPixelFormatA8B8G8R8Srgb;
        for (uint y = 0; y < destination.Height; y++)
        {
            var sourceY = (uint)(((ulong)y * source.Height) / destination.Height);
            for (uint x = 0; x < destination.Width; x++)
            {
                var sourceX = (uint)(((ulong)x * source.Width) / destination.Width);
                var sourceOffset = checked((int)(((ulong)sourceY * source.Width + sourceX) * 4));
                var destinationOffset = checked((int)x * 4);
                if (rgbaDestination)
                {
                    destinationRow[destinationOffset + 0] = sourceBytes[sourceOffset + 0];
                    destinationRow[destinationOffset + 1] = sourceBytes[sourceOffset + 1];
                    destinationRow[destinationOffset + 2] = sourceBytes[sourceOffset + 2];
                }
                else
                {
                    destinationRow[destinationOffset + 0] = sourceBytes[sourceOffset + 2];
                    destinationRow[destinationOffset + 1] = sourceBytes[sourceOffset + 1];
                    destinationRow[destinationOffset + 2] = sourceBytes[sourceOffset + 0];
                }

                destinationRow[destinationOffset + 3] = sourceBytes[sourceOffset + 3];
            }

            var destinationAddress = destination.Address + ((ulong)y * destinationPitch * 4);
            if (!ctx.Memory.TryWrite(destinationAddress, destinationRow))
            {
                return false;
            }
        }

        lock (_softwarePresenterGate)
        {
            _softwarePresenterFingerprints[fingerprintKey] = fingerprint;
        }

        TraceAgc(
            $"agc.software_presenter src=0x{source.Address:X16} {source.Width}x{source.Height} fmt={source.Format} " +
            $"dst=0x{destination.Address:X16} {destination.Width}x{destination.Height} fingerprint=0x{fingerprint:X16}");
        return true;
    }

    private static ulong ComputeFingerprint(ReadOnlySpan<byte> bytes)
    {
        const ulong fnvOffsetBasis = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        var fingerprint = fnvOffsetBasis;
        foreach (var value in bytes)
        {
            fingerprint = (fingerprint ^ value) * fnvPrime;
        }

        return fingerprint;
    }

    private static void TraceSubmittedPacket(
        CpuContext ctx,
        ulong packetAddress,
        uint dwordOffset,
        uint header,
        uint length,
        uint op,
        uint register)
    {
        TraceAgc(
            $"agc.dcb.packet dw={dwordOffset} addr=0x{packetAddress:X16} header=0x{header:X8} len={length} op=0x{op:X2} reg=0x{register:X2}");

        var payloadCount = Math.Min(length - 1, 32u);
        for (uint i = 0; i < payloadCount; i++)
        {
            if (!TryReadUInt32(ctx, packetAddress + ((ulong)(i + 1) * sizeof(uint)), out var value))
            {
                return;
            }

            TraceAgc($"agc.dcb.payload dw={dwordOffset + i + 1} value=0x{value:X8}");
        }

        if (op != ItNop ||
            register is not (RCxRegsIndirect or RShRegsIndirect or RUcRegsIndirect) ||
            length < 4 ||
            !TryReadUInt32(ctx, packetAddress + 4, out var registerCount) ||
            !TryReadUInt64(ctx, packetAddress + 8, out var registersAddress))
        {
            return;
        }

        var registerSpace = register == RCxRegsIndirect ? "cx" : register == RShRegsIndirect ? "sh" : "uc";
        var tracedCount = Math.Min(registerCount, 256u);
        TraceAgc($"agc.dcb.indirect space={registerSpace} regs=0x{registersAddress:X16} count={registerCount}");
        for (uint i = 0; i < tracedCount; i++)
        {
            var entryAddress = registersAddress + ((ulong)i * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var registerOffset) ||
                !TryReadUInt32(ctx, entryAddress + 4, out var value))
            {
                TraceAgc($"agc.dcb.indirect_read_failed space={registerSpace} index={i} addr=0x{entryAddress:X16}");
                return;
            }

            TraceAgc($"agc.dcb.reg space={registerSpace} index={i} offset=0x{registerOffset:X4} value=0x{value:X8}");
        }

        if (tracedCount != registerCount)
        {
            TraceAgc($"agc.dcb.indirect_truncated space={registerSpace} traced={tracedCount} total={registerCount}");
        }
    }

    private static bool PatchShaderProgramRegisters(CpuContext ctx, ulong headerAddress, ulong codeAddress)
    {
        if (!TryReadUInt64(ctx, headerAddress + ShaderShRegistersOffset, out var shRegistersAddress) ||
            !TryReadByte(ctx, headerAddress + ShaderTypeOffset, out var shaderType) ||
            !TryReadByte(ctx, headerAddress + ShaderNumShRegistersOffset, out var registerCount))
        {
            return false;
        }

        if (shRegistersAddress == 0 || registerCount < 2)
        {
            return false;
        }

        if (!TryReadUInt32(ctx, shRegistersAddress, out var loRegister) ||
            !TryReadUInt32(ctx, shRegistersAddress + 8, out var hiRegister))
        {
            return false;
        }

        var expectedLo = shaderType == 2 ? SpiShaderPgmLoEs : shaderType == 1 ? SpiShaderPgmLoPs : 0;
        var expectedHi = shaderType == 2 ? SpiShaderPgmHiEs : shaderType == 1 ? SpiShaderPgmHiPs : 0;
        if (expectedLo == 0 || loRegister != expectedLo || hiRegister != expectedHi)
        {
            TraceCreateShader(0, headerAddress, codeAddress, $"unexpected-registers type={shaderType} lo=0x{loRegister:X8} hi=0x{hiRegister:X8}");
            return false;
        }

        var loValue = (uint)((codeAddress >> 8) & 0xFFFF_FFFFUL);
        var hiValue = (uint)((codeAddress >> 40) & 0xFFUL);
        return TryWriteUInt32(ctx, shRegistersAddress + sizeof(uint), loValue) &&
               TryWriteUInt32(ctx, shRegistersAddress + 8 + sizeof(uint), hiValue);
    }

    private static int SetIndirectPatchAddress(CpuContext ctx, string registerSpace)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var registersAddress = ctx[CpuRegister.Rsi];
        if (commandAddress == 0 || registersAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, commandAddress + 8, (uint)(registersAddress & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(registersAddress >> 32)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.patch_{registerSpace}_addr cmd=0x{commandAddress:X16} regs=0x{registersAddress:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int AddIndirectPatchRegisters(CpuContext ctx, string registerSpace)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var registerCount = (uint)ctx[CpuRegister.Rsi];
        if (commandAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt32(ctx, commandAddress + 4, out var currentCount) ||
            !TryWriteUInt32(ctx, commandAddress + 4, currentCount + registerCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.patch_{registerSpace}_add cmd=0x{commandAddress:X16} add={registerCount} total={currentCount + registerCount}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int DcbSetRegistersIndirect(CpuContext ctx, uint packetRegister, string registerSpace)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var registersAddress = ctx[CpuRegister.Rsi];
        var registerCount = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItNop, packetRegister)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, registerCount) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(registersAddress & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(registersAddress >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_{registerSpace}_indirect buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} regs=0x{registersAddress:X16} count={registerCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    private static bool TryAllocateCommandDwords(CpuContext ctx, ulong commandBufferAddress, uint sizeDwords, out ulong commandAddress)
    {
        commandAddress = 0;
        if (sizeDwords == 0 ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCursorUpOffset, out var cursorUp) ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCursorDownOffset, out var cursorDown) ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCallbackOffset, out var callback) ||
            !TryReadUInt32(ctx, commandBufferAddress + CommandBufferReservedDwOffset, out var reservedDwords))
        {
            return false;
        }

        var availableDwords = cursorDown >= cursorUp
            ? Math.Min((cursorDown - cursorUp) / sizeof(uint), uint.MaxValue)
            : 0;
        var remainingDwords = (uint)Math.Max(availableDwords, reservedDwords) - reservedDwords;
        if (sizeDwords > remainingDwords)
        {
            TraceAgc($"agc.cmd_alloc_full buf=0x{commandBufferAddress:X16} need={sizeDwords} remaining={remainingDwords} callback=0x{callback:X16}");
            return false;
        }

        var nextCursor = cursorUp + ((ulong)sizeDwords * sizeof(uint));
        if (!ctx.TryWriteUInt64(commandBufferAddress + CommandBufferCursorUpOffset, nextCursor))
        {
            return false;
        }

        commandAddress = cursorUp;
        return true;
    }

    private static bool CopyShaderRegister(CpuContext ctx, ulong sourceAddress, ulong destinationAddress)
    {
        if (!TryReadUInt32(ctx, sourceAddress, out var offset) ||
            !TryReadUInt32(ctx, sourceAddress + sizeof(uint), out var value))
        {
            return false;
        }

        return TryWriteUInt32(ctx, destinationAddress, offset) &&
               TryWriteUInt32(ctx, destinationAddress + sizeof(uint), value);
    }

    private static bool RelocatePointerField(CpuContext ctx, ulong fieldAddress)
    {
        if (!TryReadUInt64(ctx, fieldAddress, out var relativeAddress))
        {
            return false;
        }

        if (relativeAddress == 0)
        {
            return true;
        }

        return ctx.TryWriteUInt64(fieldAddress, fieldAddress + relativeAddress);
    }

    private static int ReturnPointer(CpuContext ctx, ulong pointer)
    {
        ctx[CpuRegister.Rax] = pointer;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(int)result);
        return (int)result;
    }

    private static uint Pm4(uint lengthDwords, uint op, uint register) =>
        0xC0000000u |
        ((((ushort)lengthDwords - 2u) & 0x3FFFu) << 16) |
        ((op & 0xFFu) << 8) |
        ((register & 0x3Fu) << 2);

    private static uint Pm4Length(uint header) =>
        ((header >> 16) & 0x3FFFu) + 2u;

    private static bool TryReadByte(CpuContext ctx, ulong address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = buffer[0];
        return true;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool TryReadUInt64(CpuContext ctx, ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    private static void TraceAgc(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] {message}");
    }

    private static void TraceCreateShader(ulong destinationAddress, ulong headerAddress, ulong codeAddress, string detail)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.create_shader dst=0x{destinationAddress:X16} header=0x{headerAddress:X16} code=0x{codeAddress:X16} {detail}");
    }
}
