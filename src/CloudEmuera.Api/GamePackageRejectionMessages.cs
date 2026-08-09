/// <summary>
/// Maps game-package ingestion rejection codes (P1-03) to user-facing Chinese
/// messages so an upload/bind failure explains the actual check that failed
/// instead of a generic "游戏包不安全或不受支持。".
/// </summary>
internal static class GamePackageRejectionMessages
{
    private static readonly Dictionary<string, string> Catalog = new(StringComparer.Ordinal)
    {
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.ArchiveTooLarge] = "压缩包超过允许的大小限制。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.ArchiveFormatUnsupported] = "压缩包格式不受支持（多卷、ZIP64 或带尾随数据）。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.ArchiveCorrupt] = "压缩包已损坏，或文件内容与压缩信息不一致。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.ArchiveEncrypted] = "不支持加密压缩包，请先移除密码再上传。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.Zip64Unsupported] = "不支持 ZIP64 格式的压缩包。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.ZipMethodUnsupported] = "压缩包使用了不支持的压缩方法。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.EntryCountExceeded] = "压缩包内文件/目录数量超过限制。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.CentralDirectoryTooLarge] = "压缩包目录信息过大。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.EntryTooLarge] = "压缩包内有文件超过单文件大小限制。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.ExpandedSizeExceeded] = "解压后的总大小超过限制。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.CompressionRatioExceeded] = "压缩比异常，疑似压缩炸弹，已拒绝。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.PathDepthExceeded] = "文件路径层级过深。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.PathInvalid] = "压缩包包含非法路径（绝对路径、路径穿越或反斜杠等）。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.PathReservedName] = "压缩包包含系统保留文件名。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.PathCollision] = "压缩包存在仅大小写或 Unicode 归一化不同的路径冲突。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.PathTypeConflict] = "同一路径同时被用作文件与目录。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.LinkEntryForbidden] = "压缩包包含符号链接条目，已拒绝。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.SpecialEntryForbidden] = "压缩包包含特殊文件条目，已拒绝。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.StagingBudgetExhausted] = "暂存空间配额已满，请稍后重试。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.DataRootSpaceLow] = "服务器数据盘空间不足，无法接收上传。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.StagingIoFailed] = "服务器写入失败，请重试。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.IngestionCancelled] = "上传已取消。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.IngestionDeadlineExceeded] = "上传处理超时，请重试。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.StagedContentChanged] = "暂存内容被并发修改，请重新上传。",
        [CloudEmuera.Application.GamePackages.GamePackageRejectionCodes.IngestionCommitFailed] = "服务器保存失败，请重试。",
        ["OWNER_NOT_ACTIVE"] = "当前账户状态不允许上传游戏包。",
        ["INGESTION_NOT_READY"] = "游戏包已过期、已消费或内容摘要不匹配，请重新上传。",
        ["INGESTION_STATE_CONFLICT"] = "游戏包状态已变化，请重新上传。",
    };

    public static string Resolve(string code, string? logicalPath)
    {
        if (!Catalog.TryGetValue(code, out string? message)) message = "游戏包未通过安全检查。";
        return logicalPath is null ? message : $"{message}（{logicalPath}）";
    }
}
