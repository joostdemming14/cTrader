using System;
using System.Collections;
using System.Collections.Generic;
using cAlgo.API;

namespace cAlgo
{
    /// <summary>
    /// Adapts a cTrader <see cref="DataSeries"/> (which does not implement
    /// <c>IReadOnlyList&lt;double&gt;</c>) to <c>IReadOnlyList&lt;double&gt;</c>
    /// so it can be passed to the pure engines. Read-only; writes are not
    /// supported. Indexing and <see cref="Count"/> delegate to the underlying
    /// series, so the adapter stays valid as the series grows.
    /// </summary>
    internal sealed class SeriesAdapter : IReadOnlyList<double>
    {
        private readonly DataSeries _series;

        public SeriesAdapter(DataSeries series)
        {
            _series = series ?? throw new ArgumentNullException(nameof(series));
        }

        public double this[int index] => _series[index];

        public int Count => _series.Count;

        public IEnumerator<double> GetEnumerator()
        {
            for (int i = 0; i < _series.Count; i++) yield return _series[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
