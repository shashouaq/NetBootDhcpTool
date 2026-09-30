# Third-party notices / 第三方许可说明

## NSIS 3.12

The project compiles its Windows Setup wrapper with NSIS 3.12. The Setup script uses `SetCompressor /SOLID zlib`; it does not bundle the Full 7z payload, which remains a separate Release asset. The helper is a self-contained .NET executable embedded in the Setup wrapper.

NSIS documents its general source, plugins, docs, examples and graphics under the zlib/libpng license, with compressor-specific exceptions. Its zlib compressor also uses the zlib/libpng license. The LZMA compressor is identified by NSIS as CPL 1.0, with an explicit exception from authors Igor Pavlov and Amir Szekely. The relevant official excerpt is: “without subjecting your linked code to the terms of the Common Public license version 1.0.” The exception covers unmodified linking/binding to files from the NSIS LZMA module; changes or additions to that module remain under CPL 1.0. The project does not modify the NSIS compressor modules.

For this lightweight wrapper, both supported compressors were measured using the same NSIS 3.12 compiler, self-contained SetupHelper (38,837,509 bytes), signed 1.1.0 manifest, signature, and script inputs. LZMA produced 32,353,854 bytes in 19.39 seconds; zlib produced 33,385,172 bytes in 3.75 seconds. The difference was 1,031,318 bytes (about 3.19% of the LZMA result). Since Full 7z is downloaded separately, the wrapper's zlib build remains comfortably below the observed per-asset risk threshold and avoids using the LZMA compressor module in the shipped Setup. The project therefore selects zlib.

The compiler distribution was NSIS 3.12 from its official SourceForge package, verified against the package SHA-256 published in the WinGet package manifest. The source archive mirrors returned HTML error pages in this environment and were discarded without extraction.

Official references:

- [NSIS Appendix I: License](https://nsis.sourceforge.io/Docs/AppendixI.html)
- [NSIS 3.12 package listing](https://sourceforge.net/projects/nsis/files/NSIS%203/3.12/)
- [NSIS compression methods](https://nsis.sourceforge.io/Docs/Chapter4.html#4.8.2)

### 中文

项目使用 NSIS 3.12 编译 Windows Setup 外壳，脚本使用 `SetCompressor /SOLID zlib`。Setup 不嵌入 Full 7z；Full 7z 仍是独立 Release 附件。SetupHelper 是嵌入外壳的自包含 .NET 程序。

NSIS 说明，除压缩模块等例外外，其源码、插件、文档、示例和图形使用 zlib/libpng 许可；zlib 压缩模块也使用该许可。LZMA 压缩模块标为 CPL 1.0，但作者 Igor Pavlov 和 Amir Szekely 对链接/绑定 NSIS LZMA 模块文件提供明确例外。官方原文摘录：“without subjecting your linked code to the terms of the Common Public license version 1.0.” 该例外适用于未修改的链接/绑定；对 LZMA 模块文件进行修改或增加内容仍适用 CPL 1.0。项目没有修改 NSIS 压缩模块。

使用同一个 NSIS 3.12 编译器、38,837,509 字节自包含 SetupHelper、已签名的 1.1.0 清单、签名和脚本输入实测：LZMA Setup 为 32,353,854 字节，耗时 19.39 秒；zlib Setup 为 33,385,172 字节，耗时 3.75 秒。差值为 1,031,318 字节，约为 LZMA 结果的 3.19%。Full 7z 独立下载，因此 zlib Setup 仍远低于当前单附件风险线，且发布 Setup 不需要使用 LZMA 模块；项目选择 zlib。

编译器来自 NSIS 官方 SourceForge NSIS 3.12 包，按 WinGet 包清单发布的 SHA-256 校验。此环境下 SourceForge 的源码压缩包镜像返回了 HTML 错误页面，文件未解压或使用。
