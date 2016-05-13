﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Reactive.Subjects;
using System.Collections.Concurrent;

namespace Futurice.DataAccess
{
    public class ModelIdentifierComparer : IEqualityComparer<ModelIdentifier>
    {
        public bool Equals(ModelIdentifier x, ModelIdentifier y)
        {
            return x.Equals(y);
        }

        public int GetHashCode(ModelIdentifier obj)
        {
            return obj.GetHashCode();
        }
    }

    class OperationEntry
    {
        private readonly List<CancellationToken> _cancellationTokens = new List<CancellationToken>();
        private readonly Action _onCancelled;
        private bool IsCancelled => _cancellationTokens.Count == 0;

        public object Operation { get; private set; }

        public OperationEntry(object operation, CancellationToken initialToken, Action onCancelled)
        {
            Operation = operation;
            _onCancelled = onCancelled;
            RegisterCancellation(initialToken);
        }

        private object _lock = new Object();

        public bool TryRegisterCancellation(CancellationToken ct)
        {
            lock(_lock)
            {
                if (IsCancelled)
                {
                    return false;
                }

                RegisterCancellation(ct);
            }

            return true;
        }

        private void RegisterCancellation(CancellationToken ct)
        {
            _cancellationTokens.Add(ct);
            ct.Register(() =>
            {
                lock (_lock)
                {
                    _cancellationTokens.Remove(ct);
                    CheckIsCancelled();
                }
            });
        }

        private void CheckIsCancelled()
        {
            if (IsCancelled)
            {
                _onCancelled();
            }
        }

    }

    class OperationKey
    {
        public readonly ModelIdentifier Identifier;
        public readonly ModelSource Source;

        public OperationKey(ModelIdentifier identifier, ModelSource source)
        {
            Identifier = identifier;
            Source = source;
        }

        public override bool Equals(object obj)
        {
            var other = obj as OperationKey;
            return Identifier.Equals(other.Identifier) && Source.Equals(other.Source);
        }

        public override int GetHashCode()
        {
            return Identifier.GetHashCode() + 3 * Source.GetHashCode();
        }
    }
    
    public enum SourcePreference
    {
        /// <summary>
        /// Default value that throws an exception.
        /// </summary>
        Unknown,

        /// <summary>
        /// Requests from server only.
        /// </summary>
        Server,

        /// <summary>
        /// Requests from cache only.
        /// </summary>
        Cache, // Do we need to seperate between memory and disk ?

        /// <summary>
        /// Requests from server, if the request fails, requests from cache.
        /// </summary>
        ServerWithCacheFallback,

        /// <summary>
        /// Requests from cache, if the request fails, requests from server.
        /// </summary>
        CacheWithServerFallback,

        /// <summary>
        /// Requests from cache, when the request fails or completes, requests from server.
        /// </summary>
        FirstCacheThenServer,

        /// <summary>
        /// Doesn't make any requests, but binds to receive the result from a request started by other operations.
        /// </summary>
        Delayed, // Don't start any operations
    }
    
    public enum ModelSource
    {
        Unknown,
        Memory,
        Disk,
        Server
    }

    public interface IMemoryCache
    {
        T Get<T>(ModelIdentifier id) where T : class;
        void Set<T>(ModelIdentifier id, T model) where T : class;

    }

    /// <summary>
    /// A class that manages ongoing operations and bindings to their updates, manages a memory cache for the models, and has the logic to load models according to different settings.
    /// </summary>
    public abstract class ModelRepository
    { 
        private readonly ModelLoader _loader;
        private readonly IMemoryCache _cache;
        private readonly ConcurrentDictionary<OperationKey, OperationEntry> _ongoingOperations = new ConcurrentDictionary<OperationKey, OperationEntry>();

        public readonly Subject<IObservable<IOperationStateBase>> _operationsObserver = new Subject<IObservable<IOperationStateBase>>();
        public readonly IObservable<IObservable<IOperationStateBase>> Operations;

        public ModelRepository(ModelLoader loader, IMemoryCache cache = null)
        {
            _loader = loader;
            _cache = cache;

            Operations = _operationsObserver;
        }
        
        /// <summary>
        /// Takes the model information and operation settings and returns and stream of updates which can be accessed to read the progress and result of the operation.
        /// </summary>
        /// <typeparam name="T">Type of the model object to get.</typeparam>
        /// <param name="id">Identifier for which to get the model object.</param>
        /// <param name="source">Determines the sources from which the model is loaded from, or if an request should be started at all.</param>
        /// <returns>Stream of updates from which the progress and result of the operation can be accessed from.</returns>
        public IObservable<IOperationState<T>> Get<T>(ModelIdentifier<T> id, SourcePreference source = SourcePreference.ServerWithCacheFallback, CancellationToken ct = default(CancellationToken)) where T : class
        {

            switch (source) {
                case SourcePreference.Cache:
                    return GetFromCache<T>(id, ct);

                case SourcePreference.Server:
                    return Get<T>(id, ModelSource.Server, ct);

                case SourcePreference.ServerWithCacheFallback:
                    return GetFromServerWithCacheFallback<T>(id, ct);

                case SourcePreference.CacheWithServerFallback:
                    return GetFromCacheWithServerFallback<T>(id, ct);

                case SourcePreference.FirstCacheThenServer:
                    return GetFirstFromCacheThenServer<T>(id, ct);

                default:
                    throw new NotImplementedException("Unknown SourcePreference: " + source.ToString());
            }
        }

        private IObservable<IOperationState<T>> GetFromCache<T>(ModelIdentifier id, CancellationToken ct) where T : class {
            var result = _cache?.Get<T>(id);

            return result != null ?
                Observable.Return(new OperationState<T>(result, 100, id: id, source: ModelSource.Memory)) :
                Get<T>(id, ModelSource.Disk, ct);
        }

        private IObservable<IOperationState<T>> GetFromServerWithCacheFallback<T>(ModelIdentifier id, CancellationToken ct) where T : class
        {
            var resultFromServer = Get<T>(id, ModelSource.Server, ct);
            return resultFromServer.WithFallback(() => GetFromCache<T>(id, ct));
        }

        private IObservable<IOperationState<T>> GetFromCacheWithServerFallback<T>(ModelIdentifier id, CancellationToken ct) where T : class
        {
            var resultFromServer = GetFromCache<T>(id, ct);
            return resultFromServer.WithFallback(() => Get<T>(id, ModelSource.Server, ct));
        }

        private IObservable<IOperationState<T>> GetFirstFromCacheThenServer<T>(ModelIdentifier id, CancellationToken ct) where T : class
        {
            var resultFromCache = GetFromCache<T>(id, ct)
                .Where(it => it.Error == null && !it.IsCancelled)
                .Select(it => new OperationState<T>(it.Result, 0.5 * it.Progress, it.Error, it.IsCancelled, it.ResultSource));

            var resultFromServer = Get<T>(id, ModelSource.Server, ct)
                .Select(it => new OperationState<T>(it.Result, 0.5 * (100 + it.Progress), it.Error, it.IsCancelled, it.ResultSource));

            return resultFromCache.Merge(resultFromServer);
        }

        private IObservable<IOperationState<T>> Get<T>(ModelIdentifier id, ModelSource source, CancellationToken ct = default(CancellationToken)) where T : class
        {
            var key = new OperationKey(id, source);
            
            var entry = _ongoingOperations.AddOrUpdate(key, 
                _ => CreateOperationEntry<T>(id, source, ct, key), 
                (_, oldEntry) => oldEntry.TryRegisterCancellation(ct) 
                                    ? oldEntry 
                                    : CreateOperationEntry<T>(id, source, ct, key)
            );
            
            var operation = (IObservable<IOperationState<T>>)entry.Operation;

            return operation
                .TakeWhile(_ => !ct.IsCancellationRequested)
                .Concat(Observable.Defer(() => ct.IsCancellationRequested 
                                                ? Observable.Return(new OperationState<T>(isCancelled: true)) 
                                                : Observable.Empty<OperationState<T>>()));
        }

        private OperationEntry CreateOperationEntry<T>(ModelIdentifier id, ModelSource source, CancellationToken ct, OperationKey key) where T : class
        {
            var combinedCts = new CancellationTokenSource();
            var newOperation = GetModel<T>(id, source, combinedCts.Token);

            IDisposable connectDisposable = null;
            IDisposable subscriptionDisposable = null;

            Action onFinished = () =>
            {
                OperationEntry obj;
                _ongoingOperations.TryRemove(key, out obj);

                subscriptionDisposable.Dispose();
                connectDisposable?.Dispose();
            };

            subscriptionDisposable = newOperation.Subscribe(__ => { }, __ => onFinished(), onFinished);

            _operationsObserver.OnNext(newOperation);
            var newEntry = new OperationEntry(newOperation, ct,
                                () =>
                                {
                                    combinedCts.Cancel();
                                    onFinished();
                                }
                           );

            return newEntry;
        }

        private IObservable<IOperationState<T>> GetModel<T>(ModelIdentifier id, ModelSource source, CancellationToken ct = default(CancellationToken)) where T : class
        {
            var operation = _loader.Load(id, source, ct: ct);//.StartWith(new OperationState<T>()).Replay();
            //operation.Connect();

            if (_cache != null)
            {
                operation.WhereResultChanged().Subscribe(state => _cache.Set(state.ResultIdentifier, state.Result));
            }

            return operation
                // TODO: Should we start with an empty operationstate ?
                
                .Select(modelsState =>
                {
                    T result = null;
                    ModelIdentifier resultId = null;
                    if (id.Equals(modelsState.ResultIdentifier))
                    {
                        result = modelsState.Result as T;
                        resultId = modelsState.ResultIdentifier;
                    }
                    return new OperationState<T>(result, modelsState.Progress, modelsState.Error, modelsState.IsCancelled, modelsState.ResultSource, resultId);
                })
                .TakeWhile(s => s.Progress <= 100);

        }
    }
}
