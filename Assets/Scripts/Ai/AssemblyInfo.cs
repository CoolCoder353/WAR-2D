using System.Runtime.CompilerServices;

// The AI players' brain (v0.14). It reads only its TeamView and acts only through PlayerActions: this assembly
// references WAR2D and Unity.Mathematics alone (no Unity.Entities, Unity.Collections or Mirror), so the ECS world,
// the simulation's native arrays and the network are out of reach at compile time, and AiAssemblyTests fail if
// its code names SimData, MapStore, SimCommandQueue, EntityManager or ServerPlayer. WAR2D can't reference this
// assembly back (that would be a cycle): it reaches the brain through the IAiBrain registration in AiSeats.cs.
[assembly: InternalsVisibleTo("WAR2D.Tests.EditMode")]
[assembly: InternalsVisibleTo("WAR2D.Tests.PlayMode")]
