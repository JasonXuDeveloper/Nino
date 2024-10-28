# Nino

实用的高性能C#序列化库，包括但不限于对.NET Core 应用或Unity/Godot游戏带来令人难以置信的收益。

![build](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/Nino/.github/workflows/ci.yml?branch=main)![license](https://img.shields.io/github/license/JasonXuDeveloper/Nino)

[官网](https://nino.xgamedev.net/zh/)

## 功能列表

[![nino.nuget](https://img.shields.io/nuget/v/Nino?label=Nino)](https://www.nuget.org/packages/Nino)

- 支持**全部非托管**类型（int/float/datetime/vector/etc)

- 支持任意**Nullable**类型

- 支持任意**ICollection**类型（list/dictonary/hashset/etc）

- 支持任意**Span**类型

- 支持**自定义Nino序列化类型**

- 支持**嵌套**上述类型（dictionary<int, list<可序列化类型[]>>）

- 支持**多态**

- 支持**数据校验**

- 性能高，GC低

## 快速开始

[文档](https://nino.xgamedev.net/zh/start)

## Performance

[微基准测试](https://nino.xgamedev.net/zh/perf/micro)
