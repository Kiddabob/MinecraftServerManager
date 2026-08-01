using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public interface IProfileValidator
{
    ProfileValidationResult Validate(ServerProfile profile);
}
