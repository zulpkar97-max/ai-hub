using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;

namespace CodexLauncher
{
    internal sealed class WebViewBridge : IDisposable
    {
        internal const string DocumentUri = "https://aihub.local/index.html";
        private readonly CoreWebView2 _core;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 20 * 1024 * 1024 };
        private readonly Action<string, string, Dictionary<string, object>> _dispatch;
        private readonly Action<string, bool> _reportError;

        internal WebViewBridge(CoreWebView2 core, Action<string, string, Dictionary<string, object>> dispatch,
            Action<string, bool> reportError)
        {
            _core = core;
            _dispatch = dispatch;
            _reportError = reportError;
            _core.WebMessageReceived += OnMessageReceived;
        }

        private void OnMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!string.Equals(e.Source, DocumentUri, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                var message = _json.DeserializeObject(e.WebMessageAsJson) as Dictionary<string, object>;
                object action;
                if (message == null || !message.TryGetValue("action", out action) || string.IsNullOrEmpty(action as string)) return;
                _dispatch((string)action, AuthParser.GetStr(message, "reqId"),
                    AuthParser.GetDict(message, "data") ?? new Dictionary<string, object>());
            }
            catch (Exception ex) { _reportError("操作异常：" + ex.Message, true); }
        }

        internal void Post(string type, object payload)
        {
            try { _core.PostWebMessageAsJson(_json.Serialize(new { type, payload })); }
            catch { }
        }

        public void Dispose()
        {
            _core.WebMessageReceived -= OnMessageReceived;
        }
    }
}
