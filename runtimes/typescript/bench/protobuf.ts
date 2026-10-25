// The benchmark's values as Protocol Buffers messages (proto/bench.proto, protobuf-es), made from the values Nino
// read, field for field. Made once, outside the timing, as an application that keeps its data in protobuf messages
// would.
import { create, type DescMessage, type MessageShape } from "@bufbuild/protobuf";
import * as b from "./gen/Bench.ts";
import * as pb from "./proto/bench_pb.ts";

const vec3 = (v: b.Vec3) => create(pb.Vec3Schema, { x: v.x, y: v.y, z: v.z });

function animal(a: b.Animal | null) {
  if (a instanceof b.Puppy)
    return create(pb.AnimalSchema, { name: a.name ?? "", kind: { case: "puppy", value: { bones: a.bones, sleepy: a.sleepy } } });
  if (a instanceof b.Dog) return create(pb.AnimalSchema, { name: a.name ?? "", kind: { case: "dog", value: { bones: a.bones } } });
  if (a instanceof b.Cat) return create(pb.AnimalSchema, { name: a.name ?? "", kind: { case: "cat", value: { lives: a.lives } } });
  return create(pb.AnimalSchema, { name: a?.name ?? "" });
}

/** A Map as a protobuf map field (an object). */
function record<K extends string | number, V, W = V>(m: Map<K | null, V> | { [key: string]: V } | null, f = (v: V) => v as unknown as W):
  Record<K, W> {
  const o = {} as Record<K, W>;
  for (const [k, v] of (m instanceof Map ? m : Object.entries(m ?? {})) as Iterable<[K | null, V]>) if (k !== null) o[k] = f(v);
  return o;
}

const converters = {
  player: [pb.PlayerSchema, (p: b.Player) => create(pb.PlayerSchema, {
    id: p.id, name: p.name ?? "", level: p.level, score: p.score, online: p.online, position: vec3(p.position),
    unlocked: p.unlocked ?? [], inventory: record(p.inventory), pet: animal(p.pet),
  })],
  zoo: [pb.ZooSchema, (z: b.Zoo) => create(pb.ZooSchema, { animals: (z.animals ?? []).map(animal) })],
  orders: [pb.OrderBookSchema, (o: b.OrderBook) => create(pb.OrderBookSchema, {
    orders: (o.orders ?? []).map((x) => ({
      id: x!.id, sku: x!.sku ?? "", customer: x!.customer ?? "", quantity: x!.quantity, price: x!.price, paid: x!.paid,
      status: x!.status,
    })),
  })],
  articles: [pb.ArticleSchema, (a: b.Article) => create(pb.ArticleSchema, {
    title: a.title ?? "", author: a.author ?? "", body: a.body ?? "", summary: a.summary ?? "",
    tags: (a.tags ?? []).map((t) => t ?? ""), views: a.views,
  })],
  mesh: [pb.MeshSchema, (m: b.Mesh) => create(pb.MeshSchema, {
    name: m.name ?? "", vertices: (m.vertices ?? []).map(vec3), normals: (m.normals ?? []).map(vec3),
    indices: m.indices ?? [],
  })],
  samples: [pb.SamplesSchema, (s: b.Samples) => create(pb.SamplesSchema, {
    values: s.values ?? [], counts: s.counts ?? [], weights: s.weights ?? [],
  })],
  lookup: [pb.LookupSchema, (l: b.Lookup) => create(pb.LookupSchema, { counts: record(l.counts), names: record(l.names, (v) => v ?? "") })],
  events: [pb.EventSchema, (e: b.Event) => create(pb.EventSchema, {
    at: e.at, userId: e.userId, kind: e.kind, refs: e.refs ?? [],
  })],
  strings: [pb.StringsSchema, (s: b.Strings) => create(pb.StringsSchema, { values: (s.values ?? []).map((t) => t ?? "") })],
} as const;

/** The schema of a data file's messages, and the conversion of its values. */
export function protobuf(file: string): [DescMessage, (value: never) => MessageShape<DescMessage>] {
  const key = (file.startsWith("strings") ? "strings" : file) as keyof typeof converters;
  return converters[key] as unknown as [DescMessage, (value: never) => MessageShape<DescMessage>];
}
