using USBShare.Models;

namespace USBShare.Services;

public interface IRuleResolver
{
    RuleResolutionResult Resolve(
        UsbTopologySnapshot topology,
        IReadOnlyList<ShareRule> rules,
        IReadOnlyCollection<Guid> validRemoteIds);
}

public sealed class RuleResolver : IRuleResolver
{
    public RuleResolutionResult Resolve(
        UsbTopologySnapshot topology,
        IReadOnlyList<ShareRule> rules,
        IReadOnlyCollection<Guid> validRemoteIds)
    {
        var result = new RuleResolutionResult();
        var validRemoteSet = new HashSet<Guid>(validRemoteIds);

        var hubRules = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var deviceRules = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules.Where(rule => rule.Enabled && !string.IsNullOrWhiteSpace(rule.NodeInstanceId)))
        {
            if (rule.NodeType == RuleNodeType.Hub)
            {
                hubRules[rule.NodeInstanceId.Trim()] = rule.RemoteId;
            }
            else
            {
                deviceRules[rule.NodeInstanceId.Trim()] = rule.RemoteId;
            }
        }

        foreach (var node in topology.Nodes.Values.Where(node => node.IsShareable && !node.IsHub && !string.IsNullOrWhiteSpace(node.BusId)))
        {
            var busId = node.BusId!;

            var ancestorHubRemotes = CollectAncestorHubRemotes(node, topology.Nodes, hubRules);
            if (ancestorHubRemotes.Count > 1)
            {
                result.ConflictsByInstanceId[node.InstanceId] = "命中多个祖先Hub并且远程配置冲突，已跳过。";
                continue;
            }

            Guid? targetRemoteId = null;
            if (ancestorHubRemotes.Count == 1)
            {
                targetRemoteId = ancestorHubRemotes[0];
            }
            else if (deviceRules.TryGetValue(node.InstanceId, out var deviceRemoteId))
            {
                targetRemoteId = deviceRemoteId;
            }

            if (!targetRemoteId.HasValue)
            {
                continue;
            }

            if (!validRemoteSet.Contains(targetRemoteId.Value))
            {
                result.ConflictsByInstanceId[node.InstanceId] = "规则指向的远程不存在或已删除，已跳过。";
                continue;
            }

            result.TargetsByBusId[busId] = targetRemoteId.Value;
        }

        return result;
    }

    private static List<Guid> CollectAncestorHubRemotes(
        UsbTopologyNode leafNode,
        Dictionary<string, UsbTopologyNode> allNodes,
        Dictionary<string, Guid> hubRules)
    {
        var remoteIds = new HashSet<Guid>();
        var cursor = leafNode.ParentInstanceId;

        while (!string.IsNullOrWhiteSpace(cursor))
        {
            if (hubRules.TryGetValue(cursor, out var remoteId))
            {
                remoteIds.Add(remoteId);
            }

            if (!allNodes.TryGetValue(cursor, out var parent))
            {
                break;
            }

            cursor = parent.ParentInstanceId;
        }

        return [.. remoteIds];
    }
}
