using System.Threading.Tasks;
using Ncrust.Core.Json;
using Ncrust.Core.Net;

namespace Ncrust.Core.Api
{
    /// <summary>一次签到的结果。</summary>
    public readonly struct SignInResult
    {
        private SignInResult(bool ok, bool alreadyDone, long points, string message)
        {
            Ok = ok;
            AlreadyDone = alreadyDone;
            Points = points;
            Message = message;
        }

        public static SignInResult Success(long points, string message) =>
            new SignInResult(true, false, points, message);

        public static SignInResult Done(string message) =>
            new SignInResult(false, true, 0, message);

        public static SignInResult Failed(string message) =>
            new SignInResult(false, false, 0, message);

        /// <summary>本次签到成功。</summary>
        public bool Ok { get; }

        /// <summary>今天已经签过（成功过）。</summary>
        public bool AlreadyDone { get; }

        /// <summary>本次拿到的积分（成功时有效）。</summary>
        public long Points { get; }

        /// <summary>服务端文案（「今日已签到」「积分 +N」等）。</summary>
        public string Message { get; }
    }

    /// <summary>
    /// 网易云每日签到（领积分）。网易对旧 weapi type=0 已逐步停用（回「功能暂不支持」），
    /// 这里按顺序尝试四种形态：weapi/eapi × type 0（网页）/ 1（安卓），取第一个成功 / 已签的结果。
    /// 需登录；未登录 / 全部失败按 <see cref="SignInResult.Failed"/> 返回，不抛异常。
    /// </summary>
    public sealed class SignInApi
    {
        private readonly NcmHttp _http;

        public SignInApi(NcmHttp http) => _http = http;

        public async Task<SignInResult> DailyAsync()
        {
            var attempts = new[]
            {
                // 形态：是否 eapi、type
                (eapi: false, type: 0),
                (eapi: true, type: 0),
                (eapi: false, type: 1),
                (eapi: true, type: 1),
            };

            var lastFailed = SignInResult.Failed("签到失败");
            foreach (var attempt in attempts)
            {
                var result = await TryAsync(attempt.eapi, attempt.type).ConfigureAwait(false);
                if (result.Ok || result.AlreadyDone)
                {
                    return result;
                }

                lastFailed = result;
            }

            return lastFailed;
        }

        private async Task<SignInResult> TryAsync(bool eapi, int type)
        {
            try
            {
                var payload = "{\"type\":" + type + "}";
                var pairs = new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, string>("type", type.ToString()),
                };
                using (var response = eapi
                    ? await _http.EapiPostAsync("/api/point/dailyTask", pairs, false).ConfigureAwait(false)
                    : await _http.WeapiPostAsync("/api/point/dailyTask", payload).ConfigureAwait(false))
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return Parse(text);
                }
            }
            catch
            {
                return SignInResult.Failed("签到失败（网络异常）");
            }
        }

        private static SignInResult Parse(string text)
        {
            JsonValue json;
            try
            {
                json = JsonValue.Parse(text);
            }
            catch (JsonParseException)
            {
                return SignInResult.Failed("签到失败（响应异常）");
            }

            var code = json.GetInt("code");
            var message = json.GetString("msg") ?? string.Empty;

            // 部分旧通道返回 200 + 「功能暂不支持」：按失败处理，让上层继续尝试下一形态。
            if (code == 200 && message.Contains("不支持"))
            {
                return SignInResult.Failed(message);
            }

            switch (code)
            {
                case 200:
                    return SignInResult.Success(json.GetInt("point"), message.Length > 0 ? message : "签到成功");
                case -2:
                    return SignInResult.Done(message.Length > 0 ? message : "今日已签到");
                default:
                    return SignInResult.Failed(message.Length > 0 ? message : "签到失败（" + code + "）");
            }
        }
    }
}
