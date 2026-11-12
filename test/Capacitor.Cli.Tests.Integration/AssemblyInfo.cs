// No assembly-wide OS restriction: a class without RunOn/ExcludeOn runs on Windows too. Most carry
// RunOn(OS.Linux | OS.MacOs) because they have not yet been shown to pass there.

// Every test here spawns the real binary and talks loopback HTTP; at TUnit's default width they
// starve each other into timeout failures.
[assembly: ParallelLimiter<SubprocessLimit>]
