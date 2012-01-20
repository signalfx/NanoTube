﻿namespace NanoTube
{
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Globalization;
	using System.Linq;
	using Collections;
	using Core;
	using Net;

	/// <summary>	A metric publishing client that supports pushing over Udp to either a StatsD or StatSite listener. </summary>
	/// <remarks>
	/// The client has two modes of operation.  There are some static methods that use object pools for efficiency / performance behind the
	/// scenes and put no responsibility on the user to clean-up resources. 
	/// 
	/// If more fine-grained control of resources are needed, a new Client may
	/// be created that will generate a new UdpMessenger class, and therefore must be disposed of after use.
	/// </remarks>
	public class MetricClient : IDisposable
	{
		private readonly static ConcurrentDictionary<string, SimpleObjectPool<UdpMessenger>> _messengerPool
			= new ConcurrentDictionary<string, SimpleObjectPool<UdpMessenger>>();

		private readonly UdpMessenger _messenger;
		private readonly string _key; 
		private readonly MetricFormat _format;
		private bool _disposed;

		/// <summary>	Initializes a new instance of the MetricClient class. </summary>
		/// <exception cref="ArgumentException">	Thrown when the hostName is null or empty OR the key contains invalid characters. </exception>
		/// <param name="hostName">	The DNS hostName of the server. </param>
		/// <param name="port">	   	The port. </param>
		/// <param name="key">	   	The optional key to prefix metrics with. </param>
		/// <param name="format">  	Describes the metric format to use. </param>
		public MetricClient(string hostName, int port, string key, MetricFormat format)
		{
			if (string.IsNullOrEmpty(hostName)) { throw new ArgumentException("cannot be null or empty", "hostName"); }
			if (!key.IsValidKey()) { throw new ArgumentException("contains invalid characters", "key"); }

			_messenger = new UdpMessenger(hostName, port);
			_key = key;
			_format = format;
		}

		/// <summary>	Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources. </summary>
		public void Dispose()
		{
			if (!this._disposed)
			{
				Dispose(true);
				GC.SuppressFinalize(this);
			}
		}

		/// <summary>	Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources. </summary>
		/// <param name="disposing">	true if resources should be disposed, false if not. </param>
		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
			{
				if (null != _messenger)
				{
					_messenger.Dispose();
				}
				this._disposed = true;
			}
		}

		/// <summary>
		/// Will send the given metrics in the specified format.  The IEnumerable will be materialized, and all data will be sent together
		/// asynchronously.  This call is not appropriate if the IEnumerable is infinite.
		/// </summary>
		/// <exception cref="ArgumentException">		Thrown when the hostName is null or empty OR the key contains invalid characters. </exception>
		/// <exception cref="ArgumentNullException">	Thrown when the metrics are null. </exception>
		/// <param name="hostName">	The DNS hostName of the server. </param>
		/// <param name="port">	   	The port. </param>
		/// <param name="format">  	Describes the metric format to use. </param>
		/// <param name="key">	   	The optional key to prefix metrics with. </param>
		/// <param name="metrics"> 	The metrics. </param>
		public static void Send(string hostName, int port, MetricFormat format, string key, IEnumerable<IMetric> metrics)
		{
			if (string.IsNullOrEmpty(hostName)) { throw new ArgumentException("cannot be null or empty", "hostName"); }
			if (!key.IsValidKey()) { throw new ArgumentException("contains invalid characters", "key"); }
			if (null == metrics) { throw new ArgumentNullException("metrics"); }

			SendToServer(hostName, port, metrics.ToStrings(key, format), false);
		}

		/// <summary>
		/// Will stream the given metrics in the specified format, breaking up the metrics into 10 packets at a time, where multiple metrics may
		/// comprise a single packet.  This call is appropriate for infinite IEnumerables.
		/// </summary>
		/// <exception cref="ArgumentException">		Thrown when the hostName is null or empty OR the key contains invalid characters. </exception>
		/// <exception cref="ArgumentNullException">	Thrown when the metrics are null. </exception>
		/// <param name="hostName">	The DNS hostName of the server. </param>
		/// <param name="port">	   	The port. </param>
		/// <param name="format">  	Describes the metric format to use. </param>
		/// <param name="key">	   	The optional key to prefix metrics with. </param>
		/// <param name="metrics"> 	The metrics. </param>
		public static void Stream(string hostName, int port, MetricFormat format, string key, IEnumerable<IMetric> metrics)
		{
			if (string.IsNullOrEmpty(hostName)) { throw new ArgumentException("cannot be null or empty", "hostName"); }
			if (!key.IsValidKey()) { throw new ArgumentException("contains invalid characters", "key"); }
			if (null == metrics) { throw new ArgumentNullException("metrics"); }

			SendToServer(hostName, port, metrics.ToStrings(key, format), true);
		}

		/// <summary>	Times a given Action and reports it as a Timing metrics to the server. </summary>
		/// <remarks>	Exceptions generated by the Action are not handled. </remarks>
		/// <exception cref="ArgumentException">		Thrown when the hostName is null or empty OR the key contains invalid characters. </exception>
		/// <exception cref="ArgumentNullException">	Thrown when the action is null. </exception>
		/// <param name="hostName">	The DNS hostName of the server. </param>
		/// <param name="port">	   	The port. </param>
		/// <param name="format">  	Describes the metric format to use. </param>
		/// <param name="key">	   	The optional key to prefix metrics with. </param>
		/// <param name="action">  	The action. </param>
		public static void Time(string hostName, int port, MetricFormat format, string key, Action action)
		{
			if (string.IsNullOrEmpty(hostName)) { throw new ArgumentException("cannot be null or empty", "hostName"); }
			if (!key.IsValidKey()) { throw new ArgumentException("contains invalid characters", "key"); }
			if (null == action) { throw new ArgumentNullException("action"); }

			Stopwatch timer = null;
			try
			{
				timer = new Stopwatch();
				timer.Start();
				action();
			}
			finally
			{
				if (null != timer)
				{
					timer.Stop();
					SendToServer(hostName, port, new[] { Metric.Timing(key, timer.Elapsed.TotalSeconds).ToString(null, format) }, false);
				}
			}
		}

		private static void SendToServer(string server, int port, IEnumerable<string> metrics, bool stream)
		{
			UdpMessenger messenger = null;
			SimpleObjectPool<UdpMessenger> serverPool = null;

			try
			{
				serverPool = _messengerPool.GetOrAdd(string.Format(CultureInfo.InvariantCulture, "{0}:{1}", server, port),
					new SimpleObjectPool<UdpMessenger>(3, pool => new UdpMessenger(server, port)));
				messenger = serverPool.Pop();

				//all used up, sorry!
				if (null == messenger) { return; }

				if (stream)
				{
					messenger.StreamMetrics(metrics);
				}
				else
				{
					messenger.SendMetrics(metrics);
				}
			}
			finally
			{
				if (null != serverPool && null != messenger) { serverPool.Push(messenger); }
			}
		}

		/// <summary>	Will send the given metric in the specified format. </summary>
		/// <exception cref="ArgumentNullException">	Thrown when the metric is null. </exception>
		/// <param name="metric">	The metric. </param>
		public void Send(IMetric metric)
		{
			if (null == metric) { throw new ArgumentNullException("metric"); }

			_messenger.SendMetrics(new[] { metric.ToString(_key, _format) });
		}

		/// <summary>
		/// Will send the given metrics in the specified format.  The IEnumerable will be materialized, and all data will be sent together
		/// asynchronously.  This call is not appropriate if the IEnumerable is infinite.
		/// </summary>
		/// <exception cref="ArgumentNullException">	Thrown when the metrics are null. </exception>
		/// <param name="metrics">	The metrics. </param>
		public void Send(IEnumerable<IMetric> metrics)
		{
			if (null == metrics) { throw new ArgumentNullException("metrics"); }

			_messenger.SendMetrics(metrics.ToStrings(_key, _format));
		}

		/// <summary>
		/// Will stream the given metrics in the specified format, breaking up the metrics into 10 packets at a time, where multiple metrics may
		/// comprise a single packet.  This call is appropriate for infinite IEnumerables.
		/// </summary>
		/// <exception cref="ArgumentNullException">	Thrown when the metrics are null. </exception>
		/// <param name="metrics">	The metrics. </param>
		public void Stream(IEnumerable<IMetric> metrics)
		{
			if (null == metrics) { throw new ArgumentNullException("metrics"); }

			_messenger.StreamMetrics(metrics.ToStrings(_key, _format));
		}
	}
}