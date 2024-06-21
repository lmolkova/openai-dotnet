using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using OpenAI.Custom.Common.Instrumentation;

#nullable enable

namespace OpenAI.Chat;

/// <summary>
/// Implementation of collection abstraction over streaming chat updates.
/// </summary>
internal class AsyncStreamingChatCompletionUpdateCollection : AsyncResultCollection<StreamingChatCompletionUpdate>
{
    private readonly Func<Task<ClientResult>> _getResultAsync;
    private readonly StreamingScope _streamingScope;

    public AsyncStreamingChatCompletionUpdateCollection(Func<Task<ClientResult>> getResultAsync, StreamingScope scope) : base()
    {
        Argument.AssertNotNull(getResultAsync, nameof(getResultAsync));

        _getResultAsync = getResultAsync;
        _streamingScope = scope;
    }

    public override IAsyncEnumerator<StreamingChatCompletionUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        return new AsyncStreamingChatUpdateEnumerator(_getResultAsync, this, _streamingScope, cancellationToken);
    }

    private sealed class AsyncStreamingChatUpdateEnumerator : IAsyncEnumerator<StreamingChatCompletionUpdate>
    {
        private const string _terminalData = "[DONE]";

        private readonly Func<Task<ClientResult>> _getResultAsync;
        private readonly AsyncStreamingChatCompletionUpdateCollection _enumerable;
        private readonly StreamingScope _streamingScope;
        private readonly CancellationToken _cancellationToken;

        // These enumerators represent what is effectively a doubly-nested
        // loop over the outer event collection and the inner update collection,
        // i.e.:
        //   foreach (var sse in _events) {
        //       // get _updates from sse event
        //       foreach (var update in _updates) { ... }
        //   }
        private IAsyncEnumerator<ServerSentEvent>? _events;
        private IEnumerator<StreamingChatCompletionUpdate>? _updates;

        private StreamingChatCompletionUpdate? _current;
        private bool _started;

        public AsyncStreamingChatUpdateEnumerator(Func<Task<ClientResult>> getResultAsync,
            AsyncStreamingChatCompletionUpdateCollection enumerable,
            StreamingScope scope,
            CancellationToken cancellationToken)
        {
            Debug.Assert(getResultAsync is not null);
            Debug.Assert(enumerable is not null);

            _getResultAsync = getResultAsync!;
            _enumerable = enumerable!;
            _streamingScope = scope;
            _cancellationToken = cancellationToken;
            _cancellationToken.Register(_streamingScope.RecordCancellation);
        }

        StreamingChatCompletionUpdate IAsyncEnumerator<StreamingChatCompletionUpdate>.Current
            => _current!;

        async ValueTask<bool> IAsyncEnumerator<StreamingChatCompletionUpdate>.MoveNextAsync()
        {
            if (_events is null && _started)
            {
                _streamingScope.RecordCancellation();
                throw new ObjectDisposedException(nameof(AsyncStreamingChatUpdateEnumerator));
            }

            _cancellationToken.ThrowIfCancellationRequested();
            _events ??= await CreateEventEnumeratorAsync().ConfigureAwait(false);
            _started = true;

            if (_updates is not null && _updates.MoveNext())
            {
                _current = _updates.Current;
                _streamingScope?.RecordChunk(_current);
                return true;
            }

            if (await _events.MoveNextAsync().ConfigureAwait(false))
            {
                if (_events.Current.Data == _terminalData)
                {
                    _streamingScope.Dispose();
                    _current = default;
                    return false;
                }

                using JsonDocument doc = JsonDocument.Parse(_events.Current.Data);
                var updates = StreamingChatCompletionUpdate.DeserializeStreamingChatCompletionUpdates(doc.RootElement);
                _updates = updates.GetEnumerator();

                if (_updates.MoveNext())
                {
                    _current = _updates.Current;
                    _streamingScope?.RecordChunk(_current);
                    return true;
                }
            }

            _streamingScope.Dispose();
            _current = default;
            return false;
        }

        private async Task<IAsyncEnumerator<ServerSentEvent>> CreateEventEnumeratorAsync()
        {
            ClientResult result = await _getResultAsync().ConfigureAwait(false);
            PipelineResponse response = result.GetRawResponse();
            _enumerable.SetRawResponse(response);

            if (response.ContentStream is null)
            {
                throw new InvalidOperationException("Unable to create result from response with null ContentStream");
            }

            AsyncServerSentEventEnumerable enumerable = new(response.ContentStream);
            return enumerable.GetAsyncEnumerator(_cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore().ConfigureAwait(false);

            GC.SuppressFinalize(this);
        }

        private async ValueTask DisposeAsyncCore()
        {
            _streamingScope.Dispose();
            if (_events is not null)
            {
                await _events.DisposeAsync().ConfigureAwait(false);
                _events = null;

                // Dispose the response so we don't leave the unbuffered
                // network stream open.
                PipelineResponse response = _enumerable.GetRawResponse();
                response.Dispose();
            }
        }
    }
}
