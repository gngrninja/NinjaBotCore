using Xunit;

namespace NinjaBotCore.Tests
{
    // Ensures tests that mutate DatabaseConfigurator static state do not run in parallel
    [CollectionDefinition("DatabaseConfigurator", DisableParallelization = true)]
    public class DatabaseConfiguratorTestCollection
    {
    }
}
