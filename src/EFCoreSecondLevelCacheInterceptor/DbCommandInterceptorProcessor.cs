using System;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EFCoreSecondLevelCacheInterceptor
{
    /// <summary>
    /// Helps processing SecondLevelCacheInterceptor
    /// </summary>
    public interface IDbCommandInterceptorProcessor
    {
        /// <summary>
        /// Reads data from cache or cache it and then returns the result
        /// </summary>
        T ProcessExecutedCommands<T>(DbCommand command, DbContext context, T result);

        /// <summary>
        /// Adds command's data to the cache
        /// </summary>
        T ProcessExecutingCommands<T>(DbCommand command, DbContext context, T result);
    }

    /// <summary>
    /// Helps processing SecondLevelCacheInterceptor
    /// </summary>
    public class DbCommandInterceptorProcessor : IDbCommandInterceptorProcessor
    {
        private readonly IEFCacheServiceProvider _cacheService;
        private readonly IEFCacheDependenciesProcessor _cacheDependenciesProcessor;
        private readonly IEFCacheKeyProvider _cacheKeyProvider;
        private readonly IEFCachePolicyParser _cachePolicyParser;
        private readonly IEFDebugLogger _logger;

        /// <summary>
        /// Helps processing SecondLevelCacheInterceptor
        /// </summary>
        public DbCommandInterceptorProcessor(
            IEFDebugLogger logger,
            IEFCacheServiceProvider cacheService,
            IEFCacheDependenciesProcessor cacheDependenciesProcessor,
            IEFCacheKeyProvider cacheKeyProvider,
            IEFCachePolicyParser cachePolicyParser)
        {
            _cacheService = cacheService;
            _cacheDependenciesProcessor = cacheDependenciesProcessor;
            _cacheKeyProvider = cacheKeyProvider;
            _cachePolicyParser = cachePolicyParser;
            _logger = logger;
        }

        /// <summary>
        /// Reads data from cache or cache it and then returns the result
        /// </summary>
        public T ProcessExecutedCommands<T>(DbCommand command, DbContext context, T result)
        {
            if (result is EFTableRowsDataReader rowsReader)
            {
                _logger.LogDebug(CacheableEventId.CacheHit, $"Returning the cached TableRows[{rowsReader.TableName}].");
                return result;
            }

            if (_cacheDependenciesProcessor.InvalidateCacheDependencies(command, context, new EFCachePolicy()))
            {
                return result;
            }

            var cachePolicy = _cachePolicyParser.GetEFCachePolicy(command.CommandText);
            if (cachePolicy == null)
            {
                return result;
            }

            var efCacheKey = _cacheKeyProvider.GetEFCacheKey(command, context, cachePolicy);

            if (result is int data)
            {
                _cacheService.InsertValue(efCacheKey, new EFCachedData { NonQuery = data }, cachePolicy);
                _logger.LogDebug(CacheableEventId.QueryResultCached, $"[{data}] added to the cache[{efCacheKey}].");
                return result;
            }

            if (result is DbDataReader dataReader)
            {
                EFTableRows tableRows;
                using (var dbReaderLoader = new EFDataReaderLoader(dataReader))
                {
                    tableRows = dbReaderLoader.LoadAndClose();
                }

                _cacheService.InsertValue(efCacheKey, new EFCachedData { TableRows = tableRows }, cachePolicy);
                _logger.LogDebug(CacheableEventId.QueryResultCached, $"TableRows[{tableRows.TableName}] added to the cache[{efCacheKey}].");
                return (T)(object)new EFTableRowsDataReader(tableRows);
            }

            if (result is object)
            {
                _cacheService.InsertValue(efCacheKey, new EFCachedData { Scalar = result }, cachePolicy);
                _logger.LogDebug(CacheableEventId.QueryResultCached, $"[{result}] added to the cache[{efCacheKey}].");
                return result;
            }

            return result;
        }

        /// <summary>
        /// Reads command's data from the cache, if any.
        /// </summary>
        public T ProcessExecutingCommands<T>(DbCommand command, DbContext context, T result)
        {
            var cachePolicy = _cachePolicyParser.GetEFCachePolicy(command.CommandText);
            if (cachePolicy == null)
            {
                return result;
            }

            var efCacheKey = _cacheKeyProvider.GetEFCacheKey(command, context, cachePolicy);
            if (!(_cacheService.GetValue(efCacheKey, cachePolicy) is EFCachedData cacheResult))
            {
                _logger.LogDebug($"[{efCacheKey}] was not present in the cache.");
                return result;
            }

            if (result is InterceptionResult<DbDataReader>)
            {
                if (cacheResult.IsNull)
                {
                    _logger.LogDebug("Suppressed the result with an empty TableRows.");
                    return (T)Convert.ChangeType(InterceptionResult<DbDataReader>.SuppressWithResult(new EFTableRowsDataReader(new EFTableRows())), typeof(T));
                }

                _logger.LogDebug($"Suppressed the result with the TableRows[{cacheResult.TableRows.TableName}] from the cache[{efCacheKey}].");
                return (T)Convert.ChangeType(InterceptionResult<DbDataReader>.SuppressWithResult(new EFTableRowsDataReader(cacheResult.TableRows)), typeof(T));
            }

            if (result is InterceptionResult<int>)
            {
                int cachedResult = cacheResult.IsNull ? default : cacheResult.NonQuery;
                _logger.LogDebug($"Suppressed the result with {cachedResult} from the cache[{efCacheKey}].");
                return (T)Convert.ChangeType(InterceptionResult<int>.SuppressWithResult(cachedResult), typeof(T));
            }

            if (result is InterceptionResult<object>)
            {
                object cachedResult = cacheResult.IsNull ? default : cacheResult.Scalar;
                _logger.LogDebug($"Suppressed the result with {cachedResult} from the cache[{efCacheKey}].");
                return (T)Convert.ChangeType(InterceptionResult<object>.SuppressWithResult(cachedResult), typeof(T));
            }

            _logger.LogDebug($"Skipped the result with {result?.GetType()} type.");

            return result;
        }
    }
}