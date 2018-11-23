using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Caching;
using System.Text;
using System.Threading.Tasks;

namespace Framework.Common.Utils
{
    public static class MemoryCacheUtil
    {
        private static string _keyPrefix = "CU";
        private static object _locker = new object();
        private static MemoryCache _cache = MemoryCache.Default;

        public static int GetInt(string key, int defaultValue)
        {
            var o = _cache[key];
            if (o == null || o == DBNull.Value)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToInt32(o);
            }
            catch
            {
                return defaultValue;
            }
        }

        public static string GetString(string key)
        {
            return _cache[key] as string;
        }

        public static void Set(string key, object value)
        {
            _cache.Set(key, value, DateTimeOffset.MaxValue);
        }

        public static void Set(string key, object value, DateTimeOffset absoluteExpiration)
        {
            _cache.Set(key, value, absoluteExpiration);
        }

        public static void Remove(string key)
        {
            _cache.Remove(key);
        }

        private static object GetLocker(string cacheKey)
        {
            var lockerCacheKey = $"{_keyPrefix}_Locker_{cacheKey}";
            object lockObject = _cache.Get(lockerCacheKey);
            if (lockObject == null)
            {
                lock (_locker)
                {
                    lockObject = _cache.Get(lockerCacheKey);
                    if (lockObject == null)
                    {
                        lockObject = new object();
                        //把锁放入缓存中，若10分钟未访问，则清除此锁缓存
                        _cache.Set(lockerCacheKey, lockObject, new CacheItemPolicy() { SlidingExpiration = new TimeSpan(0, 10, 0) });
                    }
                }
            }
            return lockObject;
        }

        /// <summary>
        /// 从临时缓存中获取数据
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="cacheKey"></param>
        /// <param name="cacheTime">缓存时间，单位ms</param>
        /// <param name="func"></param>
        /// <returns></returns>
        public static T GetFromCache<T>(string cacheKey, int cacheTime, Func<T> func)
        {
            if (func == null)
            {
                throw new ArgumentException();
            }

            var data = _cache.Get(cacheKey);
            if (data != null)
            {
                //如果从缓存中取到的数据是DBNull则返回T类型的默认值。
                return data == DBNull.Value ? default(T) : (T)data;
            }
            else
            {
                var lockObject = GetLocker(cacheKey);
                lock (lockObject)
                {
                    data = _cache.Get(cacheKey);
                    if (data != null)
                    {
                        return data == DBNull.Value ? default(T) : (T)data;
                    }
                    else
                    {
                        var tempData = func.Invoke();
                        if (tempData != null)
                        {
                            _cache.Set(cacheKey, tempData, DateTimeOffset.Now.AddMilliseconds(cacheTime));
                        }
                        else
                        {
                            //防止缓存穿透
                            _cache.Set(cacheKey, DBNull.Value, DateTimeOffset.Now.AddMilliseconds(cacheTime));
                        }
                        return tempData;
                    }
                }
            }
        }


        /// <summary>
        /// 异步从临时缓存中获取数据,超时则返回上一次的数据
        /// </summary>
        /// <typeparam name="T">返回的数据类型</typeparam>
        /// <typeparam name="P">Func的参数的类型</typeparam>
        /// <param name="cacheKey">缓存key</param>
        /// <param name="cacheTime">缓存时间秒</param>
        /// <param name="func">获取数据的方法</param>
        /// <param name="param">获取数据方法的参数</param>
        /// <param name="remainTime">数据保留时间秒</param>
        /// <returns></returns>
        public static T GetFromCacheAsync<T, P>(string cacheKey, int cacheTime, Func<P, T> func, P param, int remainTime)
        {
            if (func == null)
            {
                throw new ArgumentException();
            }

            var data = _cache.Get(cacheKey);
            if (data != null)
            {
                //如果从缓存中取到的数据是DBNull则返回T类型的默认值。
                return data == DBNull.Value ? default(T) : (T)data;
            }
            else
            {
                var lockObject = GetLocker(cacheKey);
                lock (lockObject)
                {
                    data = _cache.Get(cacheKey);
                    if (data != null)
                    {
                        return data == DBNull.Value ? default(T) : (T)data;
                    }
                    else
                    {
                        var tf = new TaskFactory();
                        var paramData = new GetFromCacheAsyncParam<P> { CacheKey = cacheKey, CacheTime = cacheTime, RemainTime = remainTime, FuncParam = param }; //构造参数传入到task的Func中
                        var tk = tf.StartNew((obj) =>
                        {
                            var tempParam = (GetFromCacheAsyncParam<P>)obj;
                            var result = func.Invoke(tempParam.FuncParam);

                            var tempCacheKey1 = tempParam.CacheKey;
                            var tempCacheKey2 = $"{_keyPrefix}_RemainData_{cacheKey}";
                            if (result != null)
                            {
                                _cache.Set(tempCacheKey1, result, DateTimeOffset.Now.AddMilliseconds(tempParam.CacheTime));
                                _cache.Set(tempCacheKey2, result, new CacheItemPolicy { SlidingExpiration = TimeSpan.FromSeconds(tempParam.RemainTime) });
                            }
                            else
                            {
                                //防止缓存穿透
                                _cache.Set(tempCacheKey1, DBNull.Value, DateTimeOffset.Now.AddMilliseconds(tempParam.CacheTime));
                                _cache.Set(tempCacheKey2, result, new CacheItemPolicy { SlidingExpiration = TimeSpan.FromSeconds(tempParam.RemainTime) });
                            }

                            return result;
                        }, paramData);

                        if (tk.Wait(2000)) //最多等待2秒
                        {
                            var tempData = tk.Result;
                            return tempData;
                        }
                        else
                        {
                            //说明没能及时取得数据，从上一次数据的缓存
                            var cacheKey2 = $"{_keyPrefix}_RemainData_{cacheKey}";
                            LogUtil.Warn($"time out: {cacheKey2}"); //先记录日志，以便分析有多少请求是超时了
                            var tempData = _cache.Get(cacheKey2);
                            return tempData == DBNull.Value ? default(T) : (T)tempData;
                        }
                    }
                }
            }
        }
        private class GetFromCacheAsyncParam<P>
        {
            public string CacheKey { get; set; }
            public int CacheTime { get; set; }
            public int RemainTime { get; set; }
            public P FuncParam { get; set; }
        }

    }
}
