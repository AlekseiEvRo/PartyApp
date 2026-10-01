using Xunit;

// Каждый тест поднимает настоящее приложение с SQLite в Temp, поэтому
// запускаем их последовательно — так стабильнее и предсказуемее.
[assembly: CollectionBehavior(DisableTestParallelization = true)]