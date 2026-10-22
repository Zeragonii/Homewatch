# HomeWatch v0.3.10 service compile fix

Fixes CS0201 at HomeWatchService/Program.cs: the standalone `Process.Start(...) ?? throw ...;` expression is replaced with an explicit null check. Build defaults to 0.3.10 and the workflow adds a specific source guard. Run GitHub Actions Windows build for definitive compilation verification.
