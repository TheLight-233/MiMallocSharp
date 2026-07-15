# MiMallocSharp

> A community-maintained C# translation of [mimalloc](https://github.com/microsoft/mimalloc).

MiMallocSharp brings the core ideas and allocation paths of mimalloc to managed
.NET applications through an unsafe, low-level C# implementation. The project
targets modern .NET runtimes as well as .NET Standard 2.1 and keeps the current
public API under the `Mimalloc` namespace.

> **Important attribution**  
> This is an independent C# translation/port of the upstream mimalloc project.
> It is not the official mimalloc repository and is not affiliated with or
> endorsed by Microsoft. See [NOTICE.md](NOTICE.md) and the
> [upstream repository](https://github.com/microsoft/mimalloc).

## 项目简介

MiMallocSharp 是 mimalloc 的 C# 翻译/移植项目，目标是在 .NET 中提供
接近原始实现思路的底层内存分配能力。原始 mimalloc 项目地址为：
<https://github.com/microsoft/mimalloc>。

## Features

- C# translation of mimalloc allocation, free, heap, page, segment, and OS paths.
- Multi-targeting for `netstandard2.1`, `net8.0`, `net9.0`, and `net10.0`.
- Basic, zeroed, aligned, reallocated, and heap-scoped allocation APIs.
- Multi-threaded allocation paths with explicit process/thread lifecycle APIs.
- Unsafe implementation designed for low overhead and predictable allocation behavior.

## Project structure

```text
MiMallocSharp/
├─ src/
│  └─ MiMallocSharp/              # Allocator library source
├─ Directory.Build.props          # Shared compiler settings
├─ MiMallocSharp.sln
├─ LICENSE
├─ NOTICE.md
└─ README.md
```

## Requirements

- .NET SDK 10.0 or later.
- A platform supported by the selected .NET runtime.
- Unsafe code enabled by the project; callers should treat returned pointers and
  `IntPtr` values as unmanaged resources.

## Build

```powershell
dotnet restore MiMallocSharp.sln
dotnet build MiMallocSharp.sln -c Release
```

## Basic usage

```csharp
using System;
using Mimalloc;

Mi.ProcessInit();

try
{
    IntPtr memory = Mi.Malloc(1024);
    if (memory == IntPtr.Zero)
        throw new OutOfMemoryException();

    try
    {
        // Use the unmanaged block here.
        Console.WriteLine($"Usable size: {Mi.UsableSize(memory)} bytes");
    }
    finally
    {
        Mi.Free(memory);
    }
}
finally
{
    Mi.ProcessDone();
}
```

Available API groups include:

| Group | Examples |
| --- | --- |
| Process and thread lifecycle | `ProcessInit`, `ProcessDone`, `ThreadInit` |
| Allocation | `Malloc`, `Zalloc`, `Calloc`, `Realloc` |
| Alignment | `MallocAligned`, `ZallocAligned` |
| Introspection | `UsableSize`, `GoodSize`, `IsInHeapRegion`, `Version` |
| Heap-scoped allocation | `HeapNew`, `HeapMalloc`, `HeapZalloc`, `HeapCalloc`, `HeapDelete` |

Always release memory with the matching `Mi.Free`/heap API and do not use a
pointer after it has been freed.

## Compatibility and scope

This project is still under active translation and validation. API behavior,
edge cases, memory ordering, and performance should be compared against the
upstream mimalloc implementation before production use. The upstream project is
the source of truth for allocator semantics and security fixes.

The current C# API is intentionally close to the translated implementation and
may evolve before a stable release. Breaking changes are possible while the
translation is being synchronized with upstream mimalloc.

## Contributing

Issues and pull requests are welcome. When changing allocator internals:

1. Keep the corresponding upstream mimalloc source file in the code comment.
2. Add a reproducible validation case for observable behavior.
3. Run the Release build on every target that is available locally.
4. Include runtime, platform, and configuration context when reporting performance changes.

## License

MiMallocSharp is released under the [MIT License](LICENSE). The project is a
C# translation/port of the upstream [mimalloc](https://github.com/microsoft/mimalloc)
project; see [NOTICE.md](NOTICE.md) for attribution and project-status details.
