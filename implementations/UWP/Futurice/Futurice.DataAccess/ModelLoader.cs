﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace Futurice.DataAccess
{
    public abstract class ModelLoader
    {

        protected abstract IObservable<OperationState<IBuffer>> LoadImplementation(ModelIdentifier id, ModelSource source);

        protected abstract IObservable<OperationState<object>> ParseImplementation(ModelIdentifier id, IBuffer data);

        public IObservable<OperationState<object>> Load(ModelIdentifier id, ModelSource source)
        {
            var loadStates = LoadImplementation(id, source);

            object latestResult = null;
            return Observable.Merge(
                loadStates
                    .WhereProgressChanged()
                    .Select(loadState => new OperationState<object>(null, loadState.Progress, loadState.Error, loadState.IsCancelled)),

                loadStates
                    .WhereResultChanged()
                    .SelectMany(state => ParseImplementation(id, state.Result))
            )
            .Do(s => latestResult = s.Result ?? latestResult)
            .Select(s => new OperationState<object>(latestResult, s.Progress, s.Error, s.IsCancelled));
        }
    }

}
