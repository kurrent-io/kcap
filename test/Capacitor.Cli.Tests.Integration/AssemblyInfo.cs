// No assembly-wide OS restriction: a class that cannot run on Windows says why on its own RunOn/ExcludeOn.

// Every test here spawns the real binary and talks loopback HTTP; at TUnit's default width they
// starve each other into timeout failures.
[assembly: ParallelLimiter<SubprocessLimit>]
