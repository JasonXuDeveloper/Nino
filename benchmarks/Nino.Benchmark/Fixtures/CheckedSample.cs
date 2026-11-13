using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark;

// UntrustedTest's element, shared with its v3 port (V3/UntrustedTest.cs).

/// <summary>A raw struct (no [NinoType]) with fields Untrusted mode checks: a DateTime and a bool.</summary>
public struct CheckedSample
{
    public DateTime At;
    public double Value;
    public int Id;
    public bool Valid;
}
