// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.UserService;

public static class UserServiceExports
{
    private const int OrbisUserServiceErrorInvalidArgument = unchecked((int)0x80960005);
    private const int PrimaryUserId = 1;
    private const int InvalidUserId = -1;

    [SysAbiExport(
        Nid = "j3YMu1MVNNo",
        ExportName = "sceUserServiceInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceInitialize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "CdWp0oHWGr0",
        ExportName = "sceUserServiceGetInitialUser",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetInitialUser(CpuContext ctx)
    {
        var userIdAddress = ctx[CpuRegister.Rdi];
        if (userIdAddress == 0)
        {
            return SetReturn(ctx, OrbisUserServiceErrorInvalidArgument);
        }

        return TryWriteInt32(ctx, userIdAddress, PrimaryUserId)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "fPhymKNvK-A",
        ExportName = "sceUserServiceGetLoginUserIdList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetLoginUserIdList(CpuContext ctx)
    {
        var userIdListAddress = ctx[CpuRegister.Rdi];
        if (userIdListAddress == 0)
        {
            return SetReturn(ctx, OrbisUserServiceErrorInvalidArgument);
        }

        Span<byte> userIds = stackalloc byte[sizeof(int) * 4];
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x00..], PrimaryUserId);
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x04..], InvalidUserId);
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x08..], InvalidUserId);
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x0C..], InvalidUserId);
        return ctx.Memory.TryWrite(userIdListAddress, userIds)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool TryWriteInt32(CpuContext ctx, ulong address, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
