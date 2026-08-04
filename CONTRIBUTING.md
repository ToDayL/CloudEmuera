# Contributing

## 开发流程

1. 从 `main` 创建短生命周期分支；
2. 保持改动范围单一，并为状态机、协议或安全边界补充测试；
3. 修改需求编号时同步检查中英文需求文档；
4. 修改 HTTP、WebSocket 或 IPC 时同步更新机器契约和兼容测试；
5. 提交前运行 `./scripts/check.sh`。

## 代码约定

- C# 使用 nullable reference types，并把编译警告视为错误；
- TypeScript 开启 strict，不在实时状态路径使用无界数组；
- 不记录密码、认证令牌或用户输入全文；
- 内置上游源码可以直接修改，但必须保留原许可证/版权声明，并同步登记 `src/CloudEmuera.EmueraRuntime/MODIFICATIONS.md`；
- 新依赖必须说明用途、许可证和无法使用标准库完成的原因。
