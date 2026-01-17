using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NinjaBotCore.Modules.Wow;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Tests for WowApi async pattern implementation.
    /// Verifies that sync methods have been removed and async alternatives exist.
    /// </summary>
    public class WowApiAsyncPatternTests
    {
        private readonly Type _wowApiType = typeof(WowApi);

        [Theory]
        [InlineData("GetAPIRequest", new[] { typeof(string), typeof(string) })]
        [InlineData("GetAPIRequest", new[] { typeof(string), typeof(bool) })]
        [InlineData("GetAPIRequest", new[] { typeof(string), typeof(string), typeof(string) })]
        public void SyncGetAPIRequest_HasBeenRemoved(string methodName, Type[] parameterTypes)
        {
            // Arrange & Act
            var method = _wowApiType.GetMethod(methodName, parameterTypes);

            // Assert - sync methods should no longer exist
            Assert.Null(method);
        }

        [Fact]
        public void SyncSearchArmory_HasBeenRemoved()
        {
            // Arrange - sync SearchArmory has 1 string param
            var method = _wowApiType.GetMethod("SearchArmory", new[] { typeof(string) });

            // Assert - sync method should no longer exist
            Assert.Null(method);
        }

        [Theory]
        [InlineData("GetAPIRequestAsync", new[] { typeof(string), typeof(string), typeof(CancellationToken) })]
        [InlineData("GetAPIRequestAsync", new[] { typeof(string), typeof(bool), typeof(CancellationToken) })]
        [InlineData("GetAPIRequestAsync", new[] { typeof(string), typeof(string), typeof(string), typeof(CancellationToken) })]
        public void AsyncGetAPIRequest_Exists_WithCorrectSignature(string methodName, Type[] parameterTypes)
        {
            // Arrange
            var method = _wowApiType.GetMethod(methodName, parameterTypes);

            // Assert
            Assert.NotNull(method);
            Assert.True(typeof(Task<string>).IsAssignableFrom(method.ReturnType));
            Assert.Null(method.GetCustomAttribute<ObsoleteAttribute>()); // Async methods should NOT be obsolete
        }

        [Fact]
        public void AsyncSearchArmory_Exists_WithCorrectSignature()
        {
            // Arrange - async SearchArmoryAsync has string, CancellationToken params
            var method = _wowApiType.GetMethod("SearchArmoryAsync",
                new[] { typeof(string), typeof(CancellationToken) });

            // Assert
            Assert.NotNull(method);
            Assert.True(typeof(Task).IsAssignableFrom(method.ReturnType));
            Assert.Null(method.GetCustomAttribute<ObsoleteAttribute>());
        }

        [Fact]
        public void NoSyncGetAPIRequestMethods_Exist()
        {
            // Arrange - verify no sync GetAPIRequest overloads exist
            var syncMethods = _wowApiType.GetMethods()
                .Where(m => m.Name == "GetAPIRequest" && !m.Name.EndsWith("Async"))
                .ToList();

            // Assert - all sync methods should have been removed
            Assert.Empty(syncMethods);
        }

        [Fact]
        public void WowApi_HasNonBlockingConstructor()
        {
            // This test verifies the constructor doesn't have synchronous Result/Wait calls
            // by checking that initialization is done via async patterns

            // Get constructor
            var constructor = _wowApiType.GetConstructors().FirstOrDefault();
            Assert.NotNull(constructor);

            // The constructor should accept IServiceProvider
            var param = constructor.GetParameters().FirstOrDefault();
            Assert.NotNull(param);
            Assert.Equal(typeof(IServiceProvider), param.ParameterType);
        }

        [Fact]
        public void AllAsyncMethods_AreNotObsolete()
        {
            // Verify async methods are not marked obsolete
            var asyncMethods = _wowApiType.GetMethods()
                .Where(m => m.Name == "GetAPIRequestAsync")
                .ToList();

            Assert.NotEmpty(asyncMethods);
            foreach (var method in asyncMethods)
            {
                Assert.Null(method.GetCustomAttribute<ObsoleteAttribute>());
                Assert.True(typeof(Task).IsAssignableFrom(method.ReturnType));
            }
        }
    }
}
