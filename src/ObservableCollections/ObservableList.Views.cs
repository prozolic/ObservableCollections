﻿using ObservableCollections.Internal;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

namespace ObservableCollections
{
    public sealed partial class ObservableList<T> : IList<T>, IReadOnlyObservableList<T>
    {
        public ISynchronizedView<T, TView> CreateView<TView>(Func<T, TView> transform, bool reverse = false)
        {
            return new View<TView>(this, transform, reverse);
        }

        internal sealed class View<TView> : ISynchronizedView<T, TView>
        {
            public ISynchronizedViewFilter<T> CurrentFilter
            {
                get
                {
                    lock (SyncRoot) { return filter; }
                }
            }

            readonly ObservableList<T> source;
            readonly Func<T, TView> selector;
            readonly bool reverse;
            readonly List<(T, TView)> list;
            int filteredCount;

            ISynchronizedViewFilter<T> filter;

            public event Action<SynchronizedViewChangedEventArgs<T, TView>>? ViewChanged;
            public event Action<NotifyCollectionChangedAction>? CollectionStateChanged;

            public object SyncRoot { get; }

            public View(ObservableList<T> source, Func<T, TView> selector, bool reverse)
            {
                this.source = source;
                this.selector = selector;
                this.reverse = reverse;
                this.filter = SynchronizedViewFilter<T>.Null;
                this.SyncRoot = new object();
                lock (source.SyncRoot)
                {
                    this.list = source.list.Select(x => (x, selector(x))).ToList();
                    this.filteredCount = list.Count;
                    this.source.CollectionChanged += SourceCollectionChanged;
                }
            }

            public int Count
            {
                get
                {
                    lock (SyncRoot)
                    {
                        return filteredCount;
                    }
                }
            }

            public void AttachFilter(ISynchronizedViewFilter<T> filter, bool invokeAddEventForCurrentElements = false)
            {
                lock (SyncRoot)
                {
                    this.filter = filter;
                    for (var i = 0; i < list.Count; i++)
                    {
                        var (value, view) = list[i];
                        if (invokeAddEventForCurrentElements)
                        {
                            filter.InvokeOnAdd(value, view, i);
                        }
                        else
                        {
                            filter.InvokeOnAttach(value, view);
                        }
                    }
                }
            }

            public void ResetFilter(Action<T>? resetAction)
            {
                lock (SyncRoot)
                {
                    this.filter = SynchronizedViewFilter<T>.Null;
                    if (resetAction != null)
                    {
                        foreach (var (item, view) in list)
                        {
                            resetAction(item, view);
                        }
                    }
                }
            }

            public INotifyCollectionChangedSynchronizedView<TView> ToNotifyCollectionChanged()
            {
                lock (SyncRoot)
                {
                    return new ListNotifyCollectionChangedSynchronizedView<T, TView>(this, null);
                }
            }

            public INotifyCollectionChangedSynchronizedView<TView> ToNotifyCollectionChanged(ICollectionEventDispatcher? collectionEventDispatcher)
            {
                lock (SyncRoot)
                {
                    return new ListNotifyCollectionChangedSynchronizedView<T, TView>(this, collectionEventDispatcher);
                }
            }

            public IEnumerator<(T, TView)> GetEnumerator()
            {
                lock (SyncRoot)
                {
                    if (!reverse)
                    {
                        foreach (var item in list)
                        {
                            if (filter.IsMatch(item.Item1, item.Item2))
                            {
                                yield return item;
                            }
                        }
                    }
                    else
                    {
                        foreach (var item in list.AsEnumerable().Reverse())
                        {
                            if (filter.IsMatch(item.Item1, item.Item2))
                            {
                                yield return item;
                            }
                        }
                    }
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public void Dispose()
            {
                this.source.CollectionChanged -= SourceCollectionChanged;
            }

            private void SourceCollectionChanged(in NotifyCollectionChangedEventArgs<T> e)
            {
                lock (SyncRoot)
                {
                    switch (e.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                            // Add
                            if (e.NewStartingIndex == list.Count)
                            {
                                if (e.IsSingleItem)
                                {
                                    var v = (e.NewItem, selector(e.NewItem));
                                    list.Add(v);
                                    this.InvokeOnAdd(ref filteredCount, ViewChanged, v, e.NewStartingIndex);
                                }
                                else
                                {
                                    var i = e.NewStartingIndex;
                                    foreach (var item in e.NewItems)
                                    {
                                        var v = (item, selector(item));
                                        list.Add(v);
                                        if (filter.IsMatch(v))
                                        {
                                            filteredCount++;
                                        }
                                        filter.InvokeOnAdd(v, i++);
                                    }
                                }
                            }
                            // Insert
                            else
                            {
                                if (e.IsSingleItem)
                                {
                                    var v = (e.NewItem, selector(e.NewItem));
                                    list.Insert(e.NewStartingIndex, v);
                                    filter.InvokeOnAdd(v, e.NewStartingIndex);
                                }
                                else
                                {
                                    // inefficient copy, need refactoring
                                    var newArray = new (T, TView)[e.NewItems.Length];
                                    var span = e.NewItems;
                                    for (var i = 0; i < span.Length; i++)
                                    {
                                        var v = (span[i], selector(span[i]));
                                        newArray[i] = v;
                                        filter.InvokeOnAdd(v, e.NewStartingIndex + i);
                                    }
                                    list.InsertRange(e.NewStartingIndex, newArray);
                                }
                            }
                            break;
                        case NotifyCollectionChangedAction.Remove:
                            if (e.IsSingleItem)
                            {
                                var v = list[e.OldStartingIndex];
                                list.RemoveAt(e.OldStartingIndex);
                                filter.InvokeOnRemove(v, e.OldStartingIndex);
                            }
                            else
                            {
                                var len = e.OldStartingIndex + e.OldItems.Length;
                                for (var i = e.OldStartingIndex; i < len; i++)
                                {
                                    var v = list[i];
                                    filter.InvokeOnRemove(v, e.OldStartingIndex + i);
                                }

                                list.RemoveRange(e.OldStartingIndex, e.OldItems.Length);
                            }
                            break;
                        case NotifyCollectionChangedAction.Replace:
                            // ObservableList does not support replace range
                            {
                                var v = (e.NewItem, selector(e.NewItem));
                                var ov = (e.OldItem, list[e.OldStartingIndex].Item2);
                                list[e.NewStartingIndex] = v;
                                filter.InvokeOnReplace(v, ov, e.NewStartingIndex);
                                break;
                            }
                        case NotifyCollectionChangedAction.Move:
                            {
                                var removeItem = list[e.OldStartingIndex];
                                list.RemoveAt(e.OldStartingIndex);
                                list.Insert(e.NewStartingIndex, removeItem);

                                filter.InvokeOnMove(removeItem, e.NewStartingIndex, e.OldStartingIndex);
                            }
                            break;
                        case NotifyCollectionChangedAction.Reset:
                            list.Clear();
                            filter.InvokeOnReset();
                            break;
                        default:
                            break;
                    }

                    RoutingCollectionChanged?.Invoke(e);
                    CollectionStateChanged?.Invoke(e.Action);
                }
            }
        }
    }
}
