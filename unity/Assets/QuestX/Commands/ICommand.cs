using QuestX.Protos.Generated;

namespace QuestX.Commands
{
    /// <summary>
    /// Interface for individual command implementations
    /// </summary>
    public interface ICommand
    {
        /// <summary>
        /// Gets the nice name of the command
        /// </summary>
        string CommandNiceName { get; }

        /// <summary>
        /// Executes this command with the provided command data
        /// </summary>
        /// <param name="receivedCommand">The command data received from the robot</param>
        void Execute(ProtobufQuestXCommand receivedCommand);
    }
}
