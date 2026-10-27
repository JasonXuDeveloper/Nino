---
tags: [skill-creator]
max_turns: 30
timeout_seconds: 900
allowed_tools: [Read, Glob, Grep, Skill, Write, Edit, Bash]
---

Port this MemoryPack code to Nino v4 and keep the same behavior. Write the result to Ported.cs and explain any behavior differences in PORTING.md.

```csharp
[MemoryPackable]
public partial class Order
{
    public long Id { get; set; }
    public string Customer { get; set; } = "";
    public List<OrderLine> Lines { get; set; } = new();
    [MemoryPackIgnore] public decimal CachedTotal { get; set; }
}

[MemoryPackable]
public partial struct OrderLine
{
    public int Sku;
    public int Quantity;
    public decimal Price;
}

public static class Wire
{
    public static byte[] Write(Order o) => MemoryPackSerializer.Serialize(o);
    public static Order? Read(ReadOnlySpan<byte> data) => MemoryPackSerializer.Deserialize<Order>(data);
}
```
