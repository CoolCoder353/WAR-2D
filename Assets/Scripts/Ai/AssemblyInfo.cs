using System.Runtime.CompilerServices;

// The AI players' brain (v0.14). It reads only its TeamView and acts only through PlayerActions: this assembly
// references WAR2D and Unity.Mathematics alone (no Unity.Entities, Unity.Collections or Mirror), so the ECS world,
// the simulation's native arrays and the network are out of reach at compile time, and AiAssemblyTests fail if
// its code names SimData, MapStore, SimCommandQueue, EntityManager, ServerPlayer or the rest of the server's state (an IL scan). WAR2D can't reference this
// assembly back (that would be a cycle): it reaches the brain and director through IAiBrain and the AiBrains registration in Net/AiPlayers.cs (AiRegistration sets it at load).
[assembly: InternalsVisibleTo("WAR2D.Tests.EditMode")]
[assembly: InternalsVisibleTo("WAR2D.Tests.PlayMode")]
