using Microsoft.VisualStudio.TestTools.UnitTesting;

// Test classes run side by side, one worker per core; the methods of a class run one after another, since classes
// share static state between their methods (counters reset in [TestInitialize], pools, registrations). A class whose
// tests touch process-wide state (NinoKernels, the shared pools, unloading, collections) is [DoNotParallelize]: it
// runs after the others, alone. `-- MSTest.Parallelize.Workers=1` runs everything in sequence.
[assembly: Parallelize(Scope = ExecutionScope.ClassLevel, Workers = 0)]
