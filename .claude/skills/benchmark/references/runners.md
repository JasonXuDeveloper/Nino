# The runner pool

Part of the `benchmark` skill: which CPUs an A/B line draws, how to reach a rare one, where a floor comes from when a
floor line misses it, and what differs between VMs of one model. Read it before writing a `cpu=` filter, requesting a
probe, or reading a verdict for one CPU.

## Matching CPUs

- Match x64 runners with `cpu='EPYC|Neoverse'`, not `'7763|Neoverse'`: runners are also EPYC 9V45 and 9V74, and a
  line whose regex misses them ends its slots without measuring (the first land/cmod round got one EPYC job).
- Use `cpu='EPYC|Xeon|Neoverse'` for a change with an AVX-512 path: Xeon Platinum 8573C and 8370C (Ice Lake) runners
  come too, and Ice Lake reversed the pair permutes that won everywhere else (`optimize`). Round 105 also drew a
  Xeon 6975P-C (run 41364890698).
- Never put a space in the `cpu=` filter (`'EPYC|Xeon 8370C'`): the workflow splits the line on whitespace, and run
  41123157579's line failed on all 8 slots with git exit 128; match on a word (`'8370C'`).

## What a run draws

- The pool decides which CPUs a run draws: run 41035440735 drew no Xeon and no 9V45 in 16 x64 slots, so a probe line
  for `'Xeon|EPYC 9V'` measured nothing. List the CPUs of a run's jobs before reading a verdict (a job that missed
  says `##[notice] ... does not match`), and queue the line again for a CPU it missed. Round 105's full-suite run
  (41364890698) drew a 9V45 only for its floor line, so that CPU had no A/B verdict.
- The pool rarely draws a Xeon 8370C (Ice Lake): four runs in round 101 drew none, so an Ice Lake probe takes several
  runs; three lines with `cpu='Xeon|Neoverse'` (24 x64 slots, a note per copy) drew one on the first run
  (41159851472), and an A/B whose third line is `cpu='Xeon'` drew one on its first too (41164356389).

## Floors when the floor line misses

- A separate floor line for an 8370C usually misses: of 15 `cpu='8370C'` lines in rounds 103-104 only 3 drew one
  (runs 41181295487, 41188054612, 41203402942, 41205347153). Take its floor from the A/B line itself instead: each
  base round against the next base round in the same jobs (BenchmarkDotNet's REPORT tables in the job log; agents use
  `/workspace/agent-notes/r103-perf/rep.py RUN LINE`), with a case both sides run with the same code (MemoryPack's)
  as a null control. A full-suite report starts each row with a Type column: find the Method and Mean columns by the
  header (round 105's parser).

## Probes

- Build `benchmarks/Nino.Benchmark` in Release before pushing a probe (warnings are errors): run 41179183815's probe
  failed CS8618 on every Xeon job it drew, two of them 8370C.
- Confirm a probe's verdict with the case itself before landing: on the 8370C a kernel that won 0.77 in
  `TuplePackProbe` lost 4.5 % in `NinoSerializeTupleList` (`optimize`).

## One model, several VMs

- Read the CPU flags in the job log too: the same model differs by VM (some EPYC 9V74 runners hide AVX-512, the Xeon
  Platinum 8573C ones AVX512-FP16), and a case on such a VM takes the other path.
