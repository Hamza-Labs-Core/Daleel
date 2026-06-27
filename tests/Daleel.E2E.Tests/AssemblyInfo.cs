// E2E tests share one server + SQLite DB and rely on registration ordering
// (first user = admin), so they must never run in parallel.
[assembly: NUnit.Framework.NonParallelizable]
[assembly: NUnit.Framework.LevelOfParallelism(1)]
