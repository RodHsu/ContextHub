using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;

namespace Memory.DashboardTests;

// Deliberate test-only use of framework render-tree APIs: dispatches real events
// without a browser, private-handler reflection, new dependencies or application services.
#pragma warning disable BL0006
internal sealed class WorkItemEditorTestRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : Renderer(services, loggerFactory)
{
    private readonly List<Exception> _errors = [];
    private int? _root;
    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    public Task MountAsync<T>() where T : IComponent => MountAsync<T>(ParameterView.Empty);
    public Task MountAsync<T>(ParameterView parameters) where T : IComponent
        => Dispatcher.InvokeAsync(async () =>
        {
            if (_root.HasValue) throw new InvalidOperationException("A root is already mounted.");
            var instance = InstantiateComponent(typeof(T));
            _root = AssignRootComponentId(instance);
            await RenderRootComponentAsync(_root.Value, parameters);
            ThrowErrors();
        });

    public Task UpdateAsync(ParameterView parameters) => Dispatcher.InvokeAsync(async () =>
    {
        await RenderRootComponentAsync(_root ?? throw new InvalidOperationException("No root is mounted."), parameters);
        ThrowErrors();
    });

    public IReadOnlyList<WorkItemEditorElement> Elements()
    {
        IReadOnlyList<WorkItemEditorElement> Read()
        {
            if (!_root.HasValue) return [];
            var elements = new List<WorkItemEditorElement>();
            Visit(_root.Value, elements);
            return elements;
        }
        return Dispatcher.CheckAccess() ? Read() : Dispatcher.InvokeAsync(Read).GetAwaiter().GetResult();
    }

    public WorkItemEditorElement Find(string dataTestId)
        => Elements().Single(element => element.Attributes.TryGetValue("data-testid", out var value) && value?.ToString() == dataTestId);
    public Task ClickAsync(string dataTestId) => DispatchAsync(Find(dataTestId), "onclick", new MouseEventArgs());
    public Task ChangeAsync(string dataTestId, object? value) => DispatchAsync(Find(dataTestId), "onchange", new ChangeEventArgs { Value = value });
    public Task DispatchAsync(WorkItemEditorElement element, string eventName, EventArgs args)
        => Dispatcher.InvokeAsync(async () =>
        {
            if (element.Disabled) throw new InvalidOperationException("The rendered element is disabled.");
            if (!element.Events.TryGetValue(eventName, out var handler)) throw new InvalidOperationException("No rendered event handler exists.");
            await DispatchEventAsync(handler, null, args);
            ThrowErrors();
        });
    public Task FlushAsync() => Dispatcher.InvokeAsync(ThrowErrors);
    public Task RemoveAsync() => Dispatcher.InvokeAsync(() =>
    {
        if (_root.HasValue) RemoveRootComponent(_root.Value);
        _root = null;
        ThrowErrors();
    });
    public ValueTask DisposeRendererAsync() => DisposeAsync();

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    protected override void HandleException(Exception exception) => _errors.Add(exception);
    private void ThrowErrors()
    {
        if (_errors.Count != 0) throw new AggregateException(_errors);
    }

    private void Visit(int componentId, List<WorkItemEditorElement> elements)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames.Array[index];
            if (frame.FrameType == RenderTreeFrameType.Component)
            {
                Visit(frame.ComponentId, elements);
                index += frame.ComponentSubtreeLength - 1;
            }
            else if (frame.FrameType == RenderTreeFrameType.Element)
            {
                var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
                var events = new Dictionary<string, ulong>(StringComparer.Ordinal);
                for (var attributeIndex = index + 1; attributeIndex < index + frame.ElementSubtreeLength && frames.Array[attributeIndex].FrameType == RenderTreeFrameType.Attribute; attributeIndex++)
                {
                    var attribute = frames.Array[attributeIndex];
                    attributes[attribute.AttributeName] = attribute.AttributeValue;
                    if (attribute.AttributeEventHandlerId != 0) events[attribute.AttributeName] = attribute.AttributeEventHandlerId;
                }
                elements.Add(new(componentId, frame.ElementName, attributes, events,
                    Text(componentId, index + 1, index + frame.ElementSubtreeLength)));
            }
        }
    }

    private string Text(int componentId, int start, int end)
    {
        var frames = GetCurrentRenderTreeFrames(componentId); var parts = new List<string>();
        for (var index = start; index < end; index++)
        {
            var frame = frames.Array[index];
            if (frame.FrameType == RenderTreeFrameType.Text) parts.Add(frame.TextContent);
            else if (frame.FrameType == RenderTreeFrameType.Markup) parts.Add(frame.MarkupContent);
            else if (frame.FrameType == RenderTreeFrameType.Component)
            {
                var child = GetCurrentRenderTreeFrames(frame.ComponentId);
                parts.Add(Text(frame.ComponentId, 0, child.Count));
                index += frame.ComponentSubtreeLength - 1;
            }
        }
        return string.Join(" ", parts);
    }
}

internal sealed record WorkItemEditorElement(int ComponentId, string Tag,
    IReadOnlyDictionary<string, object?> Attributes, IReadOnlyDictionary<string, ulong> Events, string Text)
{
    public bool Disabled => Attributes.TryGetValue("disabled", out var value) && value is not false && value is not null;
    public object? Attribute(string name) => Attributes.TryGetValue(name, out var value) ? value : null;
}
#pragma warning restore BL0006
