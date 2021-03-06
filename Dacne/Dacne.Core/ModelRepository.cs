﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Dacne.Core
{
    public class ModelIdentifierComparer : IEqualityComparer<ModelIdentifierBase>
    {
        public bool Equals(ModelIdentifierBase x, ModelIdentifierBase y)
        {
            return x.Equals(y);
        }

        public int GetHashCode(ModelIdentifierBase obj)
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
            lock (_lock)
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
        public readonly ModelIdentifierBase Identifier;
        public readonly ModelSource Source;

        public OperationKey(ModelIdentifierBase identifier, ModelSource source)
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

    class UpdateKey
    {
        public readonly ModelIdentifierBase Identifier;
        public readonly object UpdateId;

        public UpdateKey(ModelIdentifierBase identifier, object updateId)
        {
            Identifier = identifier;
            UpdateId = updateId;
        }

        public override bool Equals(object obj)
        {
            var other = obj as UpdateKey;
            return Identifier.Equals(other.Identifier) && UpdateId.Equals(other.UpdateId);
        }

        public override int GetHashCode()
        {
            return Identifier.GetHashCode() + 3 * UpdateId.GetHashCode();
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
        T Get<T>(ModelIdentifierBase id) where T : class;
        void Set<T>(ModelIdentifierBase id, T model) where T : class;
        ModelIdentifierBase GetId(object model);
    }

    /// <summary>
    /// A class that manages ongoing operations and bindings to their updates, manages a memory cache for the models, and has the logic to load models according to different settings.
    /// </summary>
    public class ModelRepository
    {
        private readonly ModelLoaderBase _loader;
        private readonly ModelWriterBase _writer;
        private readonly IMemoryCache _cache;
        private readonly ConcurrentDictionary<OperationKey, OperationEntry> _ongoingOperations = new ConcurrentDictionary<OperationKey, OperationEntry>();

        public readonly Subject<IObservable<IOperationStateBase>> _operationsObserver = new Subject<IObservable<IOperationStateBase>>();
        public readonly IObservable<IObservable<IOperationStateBase>> Operations;

        public ModelRepository(ModelLoaderBase loader, ModelWriterBase writer = null, IMemoryCache cache = null)
        {
            _writer = writer;
            _loader = loader;
            _cache = cache;

            Operations = _operationsObserver;
        }

        #region GET

        /// <summary>
        /// Takes the model information and operation settings and returns and stream of updates which can be accessed to read the progress and result of the operation.
        /// </summary>
        /// <typeparam name="T">Type of the model object to get.</typeparam>
        /// <param name="id">Identifier for which to get the model object.</param>
        /// <param name="source">Determines the sources from which the model is loaded from, or if an request should be started at all.</param>
        /// <returns>Stream of updates from which the progress and result of the operation can be accessed from.</returns>
        public IObservable<IOperationState<T>> Get<T>(ModelIdentifier<T> id, SourcePreference source = SourcePreference.ServerWithCacheFallback, CancellationToken ct = default) where T : class
        {

            switch (source)
            {
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

        private IObservable<IOperationState<T>> GetFromCache<T>(ModelIdentifierBase id, CancellationToken ct) where T : class
        {
            var result = _cache?.Get<T>(id);

            return result != null ?
                Observable.Return(new OperationState<T>(result, 100, id: id, source: ModelSource.Memory)) :
                Get<T>(id, ModelSource.Disk, ct);
        }

        private IObservable<IOperationState<T>> GetFromServerWithCacheFallback<T>(ModelIdentifierBase id, CancellationToken ct) where T : class
        {
            var resultFromServer = Get<T>(id, ModelSource.Server, ct);
            return resultFromServer.WithFallback(() => GetFromCache<T>(id, ct));
        }

        private IObservable<IOperationState<T>> GetFromCacheWithServerFallback<T>(ModelIdentifierBase id, CancellationToken ct) where T : class
        {
            var resultFromCache = GetFromCache<T>(id, ct);
            return resultFromCache.WithFallback(() => Get<T>(id, ModelSource.Server, ct));
        }

        private IObservable<IOperationState<T>> GetFirstFromCacheThenServer<T>(ModelIdentifierBase id, CancellationToken ct) where T : class
        {
            var resultFromCache = GetFromCache<T>(id, ct)
                .Where(it => it.Error == null && !it.IsCancelled)
                .Select(it => new OperationState<T>(it.Result, 0.5 * it.Progress, it.Error, it.IsCancelled, it.ResultSource));

            var resultFromServer = Get<T>(id, ModelSource.Server, ct)
                .Select(it => new OperationState<T>(it.Result, 0.5 * (100 + it.Progress), it.Error, it.IsCancelled, it.ResultSource));

            return resultFromCache.Merge(resultFromServer);
        }

        private IObservable<IOperationState<T>> Get<T>(ModelIdentifierBase id, ModelSource source, CancellationToken ct = default) where T : class
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

        private OperationEntry CreateOperationEntry<T>(ModelIdentifierBase id, ModelSource source, CancellationToken ct, OperationKey key) where T : class
        {
            var combinedCts = new CancellationTokenSource();
            var newOperation = GetModel<T>(id, source, combinedCts.Token);

            IDisposable connectDisposable = null;
            IDisposable subscriptionDisposable = null;

            void onFinished()
            {
                _ongoingOperations.TryRemove(key, out OperationEntry obj);

                subscriptionDisposable?.Dispose();
                connectDisposable?.Dispose();
            }

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

        private IObservable<IOperationState<T>> GetModel<T>(ModelIdentifierBase id, ModelSource source, CancellationToken ct = default) where T : class
        {
            var operation = _loader.Load(id, source, ct: ct);

            operation
                .WhereResultChanged()
                .Where(state => state.ResultProgress == 100)
                .Subscribe(state =>
                {
                    var result = state.Result;
                    var resultId = state.ResultIdentifier;

                    if (id.Equals(resultId))
                    {
                        // We want to run the updates within the synced AddOrUpdate, but only if we actually have updates for this model.
                        UpdateContainer _ = null;
                        if (_updates.TryGetValue(resultId, out _) && _ != null)
                        {
                            _updates.AddOrUpdate(resultId, (UpdateContainer)null,
                                (__, modelUpdates) =>
                                {
                                    var updateableResult = result as IUpdateableModel<T>;
                                    modelUpdates.Original = updateableResult.CloneForUpdate();
                                    modelUpdates.ForEach(entry => result = entry.Update(result) as T);
                                    modelUpdates.Updated = result;
                                    return modelUpdates;
                                }
                            );
                        }
                    }
                    
                    // Result from disk probably shouldn't overwrite result from server in the memory cache?
                    if (_cache != null)
                    {
                        _cache.Set(resultId, result);
                    }
                });

            return operation
                // TODO: Should we start with an empty operationstate ?
                .Select(state =>
                {
                    var isMatch = id.Equals(state.ResultIdentifier);
                    return new OperationState<T>(
                        isMatch ? state.Result as T : null,
                        state.Progress,
                        state.Error,
                        state.IsCancelled,
                        state.ResultSource,
                        isMatch ? state.ResultIdentifier : null,
                        isMatch ? state.ResultProgress : 0
                    );
                })
                .TakeWhile(s => s.Progress <= 100);

        }

        #endregion

        #region UPDATE
        
        private readonly ConcurrentDictionary<ModelIdentifierBase, UpdateContainer> _updates = new ConcurrentDictionary<ModelIdentifierBase, UpdateContainer>();

        public void Commit((ModelIdentifierBase id, UpdateEntry update) newUpdate)
        {
            Commit(newUpdate.id, newUpdate.update);
        }

        /*
        public void Commit<T>(ModelIdentifier<T> modelIdentifier, Action<T> update, object updateToken = null) where T : class, IUpdateableModel<T>
        {
            Commit(modelIdentifier, model => { update(model); return model; }, updateToken);
        }

        public void Commit<T>(ModelIdentifier<T> modelIdentifier, Func<T, T> update, object updateToken = null) where T : class, IUpdateableModel<T>
        {
            if (updateToken == null)
            {
                updateToken = new object();
            }
        }
        */

        public void Commit(ModelIdentifierBase modelIdentifier, UpdateEntry newUpdate)
        {
            _updates.AddOrUpdate(modelIdentifier, 
                                 _ => new UpdateContainer { newUpdate },
                                (identifier, container) => {
                                    var oldUpdate = container.Where(entry => entry.Token == newUpdate.Token).FirstOrDefault();
                                    if (oldUpdate != null) {
                                        container.Remove(oldUpdate);
                                    }

                                    container.Updated = null;

                                    container.Add(newUpdate);

                                    return container;
                                }
            );

            // if SetImmediately, find model and run update. Need to cache copy of old if parser needs to know it.
        }

        public IObservable<IOperationState<object>> Push(ModelIdentifierBase id, ModelSource target, CancellationToken ct = default)
        {
            if (_updates.TryGetValue(id, out var update))
            {
                return _writer.Write(id, update, target, ct: ct);
            }

            throw new InvalidOperationException("No updates for the given model: " + id);
        }

        public IObservable<IOperationState<object>> PushAll(ModelSource target, CancellationToken ct = default)
        {
            return 
                _updates.Aggregate<KeyValuePair<ModelIdentifierBase, UpdateContainer>, IObservable<IOperationState<object>>>(
                    null,
                    (acc, update) => {
                        var writeOp = _writer.Write(update.Key, update.Value, target, ct);
                        if (acc != null)
                        {
                            return acc.Merge(writeOp);
                        }

                        return writeOp;
                    }
                );            
        }

        public ModelIdentifierBase GetIdentifier(object model)
        {
            return _cache.GetId(model);
        }

        #endregion

    }

    public enum UpdateSettings
    {
        None,
        SetImmediately,
        SetOnSync,
        OverrideOnUpdate,
    }

    public class UpdateContainer : List<UpdateEntry>
    {
        // TODO: Take into account
        public UpdateSettings Settings { get; set; }
        public object Original { get; set; }
        public object Updated { get; set; }
    }

    public class UpdateEntry
    {
        public readonly object Token;
        public readonly Func<object, object> Update;

        public UpdateEntry(object token, Func<object, object> update)
        {
            Token = token;
            Update = update;
        }
    }
}
