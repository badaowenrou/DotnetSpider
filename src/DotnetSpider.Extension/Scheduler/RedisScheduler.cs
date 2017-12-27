﻿using DotnetSpider.Core;
using DotnetSpider.Core.Scheduler;
using DotnetSpider.Core.Scheduler.Component;
using DotnetSpider.Core.Infrastructure;
using Newtonsoft.Json;
using System.Collections.Generic;
using StackExchange.Redis;
using DotnetSpider.Extension.Infrastructure;
using DotnetSpider.Core.Redial;
using System;
using Polly;
using Polly.Retry;
#if NET_CORE
#endif

namespace DotnetSpider.Extension.Scheduler
{
	/// <summary>
	/// Use Redis as url scheduler for distributed crawlers.
	/// </summary>
	public class RedisScheduler : DuplicateRemovedScheduler, IDuplicateRemover
	{
		private readonly object _locker = new object();
		private const string TasksKey = "dotnetspider:tasks";
		private const string TaskStatsKey = "dotnetspider:task-stats";
		private readonly RetryPolicy _retryPolicy = Policy.Handle<Exception>().Retry(30);
		private string _queueKey;
		private string _setKey;
		private string _itemKey;
		private string _errorCountKey;
		private string _successCountKey;
		private string _identityMd5;
		private readonly AutomicLong _successCounter = new AutomicLong(0);
		private readonly AutomicLong _errorCounter = new AutomicLong(0);
		private string _connectString;
		private RedisConnection _redisConnection;
		private ISpider _spider;

		public int BatchCount { get; set; } = 1000;

		protected override bool UseInternet { get; set; } = true;

		public RedisScheduler(string connectString)
		{
			if (string.IsNullOrEmpty(connectString) || string.IsNullOrWhiteSpace(connectString))
			{
				throw new SpiderException("Redis connect string should not be empty.");
			}
			_connectString = connectString;
			DuplicateRemover = this;
		}

		public RedisScheduler()
		{
			_connectString = Env.RedisConnectString;
			DuplicateRemover = this;
		}

		public override void Init(ISpider spider)
		{
			if (string.IsNullOrEmpty(_connectString))
			{
				throw new SpiderException("Redis connect string should not be empty.");
			}

			base.Init(spider);

			_spider = spider;

			if (string.IsNullOrEmpty(_identityMd5))
			{
				var md5 = Encrypt.Md5Encrypt(spider.Identity);
				_itemKey = $"dotnetspider:scheduler:{md5}:items";
				_setKey = $"dotnetspider:scheduler:{md5}:set";
				_queueKey = $"dotnetspider:scheduler:{md5}:queue";
				_errorCountKey = $"dotnetspider:scheduler:{md5}:numberOfFailures";
				_successCountKey = $"dotnetspider:scheduler:{md5}:numberOfSuccessful";

				_identityMd5 = md5;

				var action = new Action(() =>
				{
					_redisConnection = Cache.Instance.Get(_connectString);
					if (_redisConnection == null)
					{
						_redisConnection = new RedisConnection(_connectString);
						Cache.Instance.Set(_connectString, _redisConnection);
					}
					_redisConnection.Database.SortedSetAdd(TasksKey, spider.Identity, (long)DateTimeUtils.GetCurrentTimeStamp());
				});

				if (UseInternet)
				{
					NetworkCenter.Current.Execute("rds-init", action);
				}
				else
				{
					action();
				}
			}
		}

		public override void ResetDuplicateCheck()
		{
			var action = new Action(() =>
			{
				_redisConnection.Database.KeyDelete(_setKey);
			});
			if (UseInternet)
			{
				NetworkCenter.Current.Execute("rds-reset", action);
			}
			else
			{
				action();
			}
		}

		public virtual bool IsDuplicate(Request request)
		{
			return _retryPolicy.Execute(() =>
			{
				bool isDuplicate = _redisConnection.Database.SetContains(_setKey, request.Identity);
				if (!isDuplicate)
				{
					_redisConnection.Database.SetAdd(_setKey, request.Identity);
				}
				return isDuplicate;
			});
		}

		public override Request Poll()
		{
			if (UseInternet)
			{
				return NetworkCenter.Current.Execute("rds-poll", PollRequest);
			}
			else
			{
				return PollRequest();
			}
		}

		public override long LeftRequestsCount
		{
			get
			{
				if (UseInternet)
				{
					return NetworkCenter.Current.Execute("rds-left", () => _redisConnection.Database.ListLength(_queueKey));
				}
				else
				{
					return _redisConnection.Database.ListLength(_queueKey);
				}
			}
		}

		public override long TotalRequestsCount
		{
			get
			{
				if (UseInternet)
				{
					return NetworkCenter.Current.Execute("rds-total", () => _redisConnection.Database.SetLength(_setKey));
				}
				else
				{
					return _redisConnection.Database.SetLength(_setKey);
				}
			}
		}

		public override long SuccessRequestsCount => _successCounter.Value;

		public override long ErrorRequestsCount => _errorCounter.Value;

		public override void IncreaseSuccessCount()
		{
			_successCounter.Inc();
		}

		public override void IncreaseErrorCount()
		{
			_errorCounter.Inc();
		}

		public override void Dispose()
		{
			IsExited = true;
		}

		public override void Clear()
		{
			base.Clear();

			if (UseInternet)
			{
				NetworkCenter.Current.Execute("rds-inc-clear", () =>
				{
					_redisConnection.Database.KeyDelete(_queueKey);
					_redisConnection.Database.KeyDelete(_setKey);
					_redisConnection.Database.KeyDelete(_itemKey);
					_redisConnection.Database.KeyDelete(_successCountKey);
					_redisConnection.Database.KeyDelete(_errorCountKey);
				});
			}
			else
			{
				_redisConnection.Database.KeyDelete(_queueKey);
				_redisConnection.Database.KeyDelete(_setKey);
				_redisConnection.Database.KeyDelete(_itemKey);
				_redisConnection.Database.KeyDelete(_successCountKey);
				_redisConnection.Database.KeyDelete(_errorCountKey);
			}
		}

		public override void Import(HashSet<Request> requests)
		{
			var action = new Action(() =>
			{
				lock (_locker)
				{
					int batchCount = BatchCount;
					int cacheSize = requests.Count > batchCount ? batchCount : requests.Count;
					RedisValue[] identities = new RedisValue[cacheSize];
					HashEntry[] items = new HashEntry[cacheSize];
					int i = 0;
					int j = requests.Count % batchCount;
					int n = requests.Count / batchCount;

					foreach (var request in requests)
					{
						identities[i] = request.Identity;
						items[i] = new HashEntry(request.Identity, JsonConvert.SerializeObject(request));
						++i;
						if (i == batchCount)
						{
							--n;

							_redisConnection.Database.SetAdd(_setKey, identities);
							_redisConnection.Database.ListRightPush(_queueKey, identities);
							_redisConnection.Database.HashSet(_itemKey, items, CommandFlags.HighPriority);

							i = 0;
							if (n != 0)
							{
								identities = new RedisValue[batchCount];
								items = new HashEntry[batchCount];
							}
							else
							{
								identities = new RedisValue[j];
								items = new HashEntry[j];
							}
						}
					}

					if (i > 0)
					{
						_redisConnection.Database.SetAdd(_setKey, identities);
						_redisConnection.Database.ListRightPush(_queueKey, identities);
						_redisConnection.Database.HashSet(_itemKey, items);
					}
				}
			});
			if (UseInternet)
			{
				NetworkCenter.Current.Execute("rds-import", action);
			}
			else
			{
				action();
			}
		}

		public HashSet<Request> ToList()
		{
			HashSet<Request> requests = new HashSet<Request>();
			Request request;
			while ((request = Poll()) != null)
			{
				requests.Add(request);
			}
			return requests;
		}

		public override bool IsExited
		{
			get
			{
				try
				{
					if (UseInternet)
					{
						return NetworkCenter.Current.Execute("rds-isexited", () =>
						{
							var result = _redisConnection.Database.HashGet(TaskStatsKey, _identityMd5);
							if (result.HasValue)
							{
								return result == 1;
							}
							else
							{
								return false;
							}
						});
					}
					else
					{
						var result = _redisConnection.Database.HashGet(TaskStatsKey, _identityMd5);
						if (result.HasValue)
						{
							return result == 1;
						}
						else
						{
							return false;
						}
					}

				}
				catch
				{
					return false;
				}
			}
			set
			{
				var action = new Action(() =>
				{
					_redisConnection.Database.HashSet(TaskStatsKey, _identityMd5, value ? 1 : 0);
				});
				if (UseInternet)
				{
					NetworkCenter.Current.Execute("rds-isexited", action);
				}
				else
				{
					action();
				}
			}
		}

		protected override void PushWhenNoDuplicate(Request request)
		{
			_retryPolicy.Execute(() =>
			{
				_redisConnection.Database.ListRightPush(_queueKey, request.Identity);
				string field = request.Identity;
				string value = JsonConvert.SerializeObject(request);

				_redisConnection.Database.HashSet(_itemKey, field, value);
			});
		}

		private Request PollRequest()
		{
			return _retryPolicy.Execute(() =>
			{
				var value = DepthFirst ? _redisConnection.Database.ListRightPop(_queueKey) : _redisConnection.Database.ListLeftPop(_queueKey);

				if (!value.HasValue)
				{
					return null;
				}
				string field = value.ToString();

				string json = _redisConnection.Database.HashGet(_itemKey, field);

				if (!string.IsNullOrEmpty(json))
				{
					var result = JsonConvert.DeserializeObject<Request>(json);
					_redisConnection.Database.HashDelete(_itemKey, field);
					result.Site = _spider.Site;
					return result;
				}
				return null;
			});
		}

		#region For Test

		internal string GetQueueKey()
		{
			return _queueKey;
		}

		internal string GetSetKey()
		{
			return _setKey;
		}

		internal string GetItemKey()
		{
			return _itemKey;
		}

		internal string GetErrorCountKey()
		{
			return _errorCountKey;
		}

		internal string GetSuccessCountKey()
		{
			return _successCountKey;
		}

		#endregion
	}
}
