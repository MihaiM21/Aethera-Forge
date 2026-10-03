using Aethera.Agent.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aethera.Agent.Protocol.Tests;

public class ProtocolRoundTripTests
{
    [Fact]
    public void Command_with_ContainerCreate_round_trips()
    {
        var deadline = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        var command = new Command
        {
            CommandId = "cmd-1",
            IdempotencyKey = "deploy-42-step-3",
            Deadline = deadline,
            ContainerCreate = new ContainerCreate
            {
                Spec = new ContainerSpec { Image = "nginx:1.27", Name = "web" },
                Start = true,
            },
        };

        var parsed = Command.Parser.ParseFrom(command.ToByteArray());

        Assert.Equal(command, parsed);
        Assert.Equal(Command.RequestOneofCase.ContainerCreate, parsed.RequestCase);
        Assert.Equal("nginx:1.27", parsed.ContainerCreate.Spec.Image);
        Assert.True(parsed.ContainerCreate.Start);
        Assert.Equal(deadline, parsed.Deadline);
    }

    [Fact]
    public void ControlMessage_wrapping_a_command_round_trips()
    {
        var message = new ControlMessage
        {
            Command = new Command
            {
                CommandId = "cmd-2",
                ContainerCreate = new ContainerCreate { Spec = new ContainerSpec { Image = "redis:7" } },
            },
        };

        var parsed = ControlMessage.Parser.ParseFrom(message.ToByteArray());

        Assert.Equal(ControlMessage.PayloadOneofCase.Command, parsed.PayloadCase);
        Assert.Equal("redis:7", parsed.Command.ContainerCreate.Spec.Image);
    }

    [Fact]
    public void Service_stubs_are_generated()
    {
        // GrpcServices="Both": server base class and client stub exist for the Connect stream.
        Assert.NotNull(typeof(AgentService.AgentServiceBase));
        Assert.NotNull(typeof(AgentService.AgentServiceClient));
    }
}
