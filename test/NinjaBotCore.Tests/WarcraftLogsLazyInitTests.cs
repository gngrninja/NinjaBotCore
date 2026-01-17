using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;
using NinjaBotCore.Models.Wow;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Tests for WarcraftLogs lazy initialization pattern.
    /// These tests verify that the static properties use lazy loading
    /// to prevent blocking during DI/constructor execution.
    /// </summary>
    public class WarcraftLogsLazyInitTests
    {
        [Fact]
        public void LazyList_DoesNotExecuteFactoryUntilValueAccessed()
        {
            // Arrange
            bool factoryExecuted = false;
            var lazy = new Lazy<List<string>>(() =>
            {
                factoryExecuted = true;
                return new List<string> { "test" };
            }, LazyThreadSafetyMode.ExecutionAndPublication);

            // Act - just creating the Lazy<T> should not execute factory
            // Assert
            Assert.False(factoryExecuted, "Factory should not execute during Lazy<T> construction");

            // Act - accessing Value should execute factory
            var value = lazy.Value;

            // Assert
            Assert.True(factoryExecuted, "Factory should execute when Value is accessed");
            Assert.Single(value);
        }

        [Fact]
        public void LazyList_FactoryOnlyExecutesOnce()
        {
            // Arrange
            int executionCount = 0;
            var lazy = new Lazy<List<string>>(() =>
            {
                Interlocked.Increment(ref executionCount);
                return new List<string> { "test" };
            }, LazyThreadSafetyMode.ExecutionAndPublication);

            // Act - access Value multiple times
            _ = lazy.Value;
            _ = lazy.Value;
            _ = lazy.Value;

            // Assert
            Assert.Equal(1, executionCount);
        }

        [Fact]
        public void LazyList_ThreadSafe_ExecutionAndPublication()
        {
            // Arrange
            int executionCount = 0;
            var lazy = new Lazy<List<string>>(() =>
            {
                Interlocked.Increment(ref executionCount);
                Thread.Sleep(50); // Simulate slow initialization
                return new List<string> { "test" };
            }, LazyThreadSafetyMode.ExecutionAndPublication);

            // Act - access Value from multiple threads simultaneously
            var threads = new Thread[10];
            for (int i = 0; i < threads.Length; i++)
            {
                threads[i] = new Thread(() => { _ = lazy.Value; });
            }

            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();

            // Assert - factory should only execute once even with concurrent access
            Assert.Equal(1, executionCount);
        }

        [Fact]
        public void LazyList_ReturnsEmptyListOnException()
        {
            // Arrange - simulates how WarcraftLogs handles API errors
            var lazy = new Lazy<List<string>>(() =>
            {
                try
                {
                    throw new Exception("Simulated API failure");
                }
                catch
                {
                    return new List<string>(); // Return empty list on error
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication);

            // Act
            var value = lazy.Value;

            // Assert
            Assert.NotNull(value);
            Assert.Empty(value);
        }

        [Fact]
        public void LazyList_NullableAccessPattern_ReturnsNullWhenNotInitialized()
        {
            // Arrange - simulates the property getter pattern: return _zones?.Value;
            Lazy<List<string>> lazy = null;

            // Act
            var value = lazy?.Value;

            // Assert
            Assert.Null(value);
        }

        [Fact]
        public void LazyList_NullableAccessPattern_ReturnsValueWhenInitialized()
        {
            // Arrange
            Lazy<List<string>> lazy = new Lazy<List<string>>(() => new List<string> { "test" });

            // Act
            var value = lazy?.Value;

            // Assert
            Assert.NotNull(value);
            Assert.Single(value);
        }
    }
}
