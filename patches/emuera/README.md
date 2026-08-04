# Emuera patch queue

这里保存 CloudEmuera 对固定上游 Emuera.EM+EE 提交的补丁。补丁文件按应用顺序使用四位数字编号，例如 `0001-extract-runtime-paths.patch`。

补丁必须可通过 `git apply --check` 验证，并在说明中记录：

- 基于的上游 commit；
- 修改目的与对应需求编号；
- 是否影响原生桌面版行为；
- 兼容性测试覆盖。

