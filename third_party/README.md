# Third-party source

## Emuera.EM+EE

官方仓库以 Git submodule 固定在 `emuera-em/`：

```text
URL: https://gitlab.com/EvilMask/emuera.em.git
Commit: 2175f8a629257efb08214e093704b3a3d3d06d05
Date: 2026-07-25
```

初始化：

```bash
git submodule update --init --recursive
```

更新必须显式执行，不跟踪浮动的“最新版”：

```bash
git -C third_party/emuera-em fetch origin master
git -C third_party/emuera-em checkout --detach <reviewed-commit>
```

随后更新 `THIRD_PARTY_NOTICES.md`、运行时清单和双兼容测试集。不要在 submodule 中直接提交 CloudEmuera 修改。

