using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BoardSandbox.Core.Json;

namespace BoardSandbox.Core.Agents
{
    // One question to a chat completions endpoint (the protocol of OpenAI, which Gemini, xAI, OpenRouter, Ollama and
    // others also speak). The answer comes back whole in Reply.
    public sealed class ApiAgentRun : AgentRun
    {
        private const int MaxErrorLength = 600;

        private static readonly HttpClient Client = CreateClient();

        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();

        public ApiAgentRun(string endpoint, string model, string key, AgentRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri address) || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
            {
                Complete(false, "'" + endpoint + "' is not a web address.");
                return;
            }
            if (request.TimeoutSeconds > 0)
                _cancel.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

            string body = Body(model, request).ToJson();
            Post(AgentLineKind.Notice, "Asking " + model + " at " + address.Host + " …");
            Task.Run(() => Ask(address, key, body));
        }

        public static JsonValue Body(string model, AgentRequest request)
        {
            JsonValue messages = JsonValue.NewArray();
            if (request.System.Length > 0)
                messages.Add(JsonValue.NewObject().Set("role", "system").Set("content", request.System));
            messages.Add(JsonValue.NewObject().Set("role", "user").Set("content", request.Prompt));
            return JsonValue.NewObject().Set("model", model ?? string.Empty).Set("messages", messages).Set("stream", false);
        }

        private async Task Ask(Uri address, string key, string body)
        {
            try
            {
                using (HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, address))
                {
                    message.Content = new StringContent(body, new UTF8Encoding(false), "application/json");
                    if (!string.IsNullOrEmpty(key))
                        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    using (HttpResponseMessage response = await Client.SendAsync(message, _cancel.Token).ConfigureAwait(false))
                    {
                        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            Complete(false, "The service answered " + (int)response.StatusCode + ": " + ErrorText(text));
                            return;
                        }
                        string reply = ReplyText(text, out string problem);
                        if (reply == null)
                            Complete(false, problem);
                        else
                            Complete(true, string.Empty, reply);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Complete(false, Cancelled ? "Cancelled." : "The service did not answer in time.");
            }
            catch (HttpRequestException e)
            {
                Complete(false, "The service could not be reached: " + Innermost(e));
            }
            catch (Exception e)
            {
                Complete(false, "The request failed: " + Innermost(e));
            }
        }

        // The text of the first choice, or null with the reason.
        public static string ReplyText(string responseJson, out string problem)
        {
            problem = string.Empty;
            if (!JsonValue.TryParse(responseJson, out JsonValue json, out string error))
            {
                problem = "The service sent something that is not JSON: " + error;
                return null;
            }
            // Some services wrap errors in a list.
            if (json.IsArray && json.Count > 0)
                json = json[0];
            if (json.Has("error"))
            {
                problem = "The service reported an error: " + ErrorText(responseJson);
                return null;
            }
            JsonValue content = json["choices"][0]["message"]["content"];
            if (content.IsString)
                return content.AsString();
            if (content.IsArray)
            {
                StringBuilder builder = new StringBuilder();
                foreach (JsonValue part in content.Items)
                {
                    if (part["text"].IsString)
                        builder.Append(part["text"].AsString());
                }
                if (builder.Length > 0)
                    return builder.ToString();
            }
            problem = "The service sent an answer without text.";
            return null;
        }

        private static string ErrorText(string body)
        {
            string text = body ?? string.Empty;
            if (JsonValue.TryParse(text, out JsonValue json, out string _))
            {
                if (json.IsArray && json.Count > 0)
                    json = json[0];
                JsonValue error = json["error"];
                if (error.IsString)
                    text = error.AsString();
                else if (error["message"].IsString)
                    text = error["message"].AsString();
            }
            text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return text.Length > MaxErrorLength ? text.Substring(0, MaxErrorLength) + " …" : text;
        }

        private static string Innermost(Exception e)
        {
            while (e.InnerException != null)
                e = e.InnerException;
            return e.Message;
        }

        private static HttpClient CreateClient()
        {
            // Each request carries its own time limit.
            return new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        protected override void Stop()
        {
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            Complete(false, "Cancelled.");
        }

        protected override void Release()
        {
            _cancel.Dispose();
        }
    }
}
