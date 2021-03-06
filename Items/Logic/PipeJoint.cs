using Godot;
using static Godot.Mathf;
using static SteelMath;
using System.Collections.Generic;



public class PipeJoint : PipeCoreLogic {
	private bool InitiallyFilledFriends = false;

	Spatial Position1;
	Spatial Position2;
	Spatial Position3;
	Spatial Position4;
	Spatial Position5;
	Spatial Position6;

	MeshInstance FirstEndMesh;
	CollisionShape FirstEndCollision;
	StaticBody FirstOpenEnd;

	MeshInstance SecondEndMesh;
	CollisionShape SecondEndCollision;
	StaticBody SecondOpenEnd;

	MeshInstance ThirdEndMesh;
	CollisionShape ThirdEndCollision;
	StaticBody ThirdOpenEnd;

	MeshInstance ForthEndMesh;
	CollisionShape ForthEndCollision;
	StaticBody ForthOpenEnd;

	MeshInstance FifthEndMesh;
	CollisionShape FifthEndCollision;
	StaticBody FifthOpenEnd;

	MeshInstance SixthEndMesh;
	CollisionShape SixthEndCollision;
	StaticBody SixthOpenEnd;

	public override void _Ready() {
		System = new PipeSystem(this);
		Friends = new HashSet<PipeCoreLogic>();

		Position1 = GetNode<Spatial>("Positions/Position1");
		Position2 = GetNode<Spatial>("Positions/Position2");
		Position3 = GetNode<Spatial>("Positions/Position3");
		Position4 = GetNode<Spatial>("Positions/Position4");
		Position5 = GetNode<Spatial>("Positions/Position5");
		Position6 = GetNode<Spatial>("Positions/Position6");

		FirstEndMesh = GetNode<MeshInstance>("FirstEndMesh");
		FirstEndCollision = GetNode<CollisionShape>("FirstEndCollision");
		FirstOpenEnd = GetNode<StaticBody>("FirstOpenEnd");

		SecondEndMesh = GetNode<MeshInstance>("SecondEndMesh");
		SecondEndCollision = GetNode<CollisionShape>("SecondEndCollision");
		SecondOpenEnd = GetNode<StaticBody>("SecondOpenEnd");

		ThirdEndMesh = GetNode<MeshInstance>("ThirdEndMesh");
		ThirdEndCollision = GetNode<CollisionShape>("ThirdEndCollision");
		ThirdOpenEnd = GetNode<StaticBody>("ThirdOpenEnd");

		ForthEndMesh = GetNode<MeshInstance>("ForthEndMesh");
		ForthEndCollision = GetNode<CollisionShape>("ForthEndCollision");
		ForthOpenEnd = GetNode<StaticBody>("ForthOpenEnd");

		FifthEndMesh = GetNode<MeshInstance>("FifthEndMesh");
		FifthEndCollision = GetNode<CollisionShape>("FifthEndCollision");
		FifthOpenEnd = GetNode<StaticBody>("FifthOpenEnd");

		SixthEndMesh = GetNode<MeshInstance>("SixthEndMesh");
		SixthEndCollision = GetNode<CollisionShape>("SixthEndCollision");
		SixthOpenEnd = GetNode<StaticBody>("SixthOpenEnd");

		CallDeferred(nameof(GridUpdate));

		base._Ready();
	}


	public override void GridUpdate() {
		HashSet<PipeCoreLogic> OriginalFriends = Friends;
		Friends = new HashSet<PipeCoreLogic>();

		PhysicsDirectSpaceState State = GetWorld().DirectSpaceState;
		Godot.Collections.Dictionary Results = State.IntersectRay(
			Translation,
			Position1.GlobalTransform.origin,
			new Godot.Collections.Array {
				this,
				FirstOpenEnd,
				Game.PossessedPlayer.ValueOr(() => null)
			},
			2 | 4
		);
		if(Results.Count > 0 && Results["collider"] is OpenEnd) {
			FirstEndMesh.Show();
			FirstEndCollision.Disabled = false;
			System.Consume(((OpenEnd)Results["collider"]).Parent.System);
			Friends.Add(((OpenEnd)Results["collider"]).Parent);
		}
		else {
			FirstEndMesh.Hide();
			FirstEndCollision.Disabled = true;
		}

		Results = State.IntersectRay(
			Translation,
			Position2.GlobalTransform.origin,
			new Godot.Collections.Array {
				this,
				SecondOpenEnd,
				Game.PossessedPlayer.ValueOr(() => null)
			},
			2 | 4
		);
		if(Results.Count > 0 && Results["collider"] is OpenEnd) {
			SecondEndMesh.Show();
			SecondEndCollision.Disabled = false;
			System.Consume(((OpenEnd)Results["collider"]).Parent.System);
			Friends.Add(((OpenEnd)Results["collider"]).Parent);
		}
		else {
			SecondEndMesh.Hide();
			SecondEndCollision.Disabled = true;
		}

		Results = State.IntersectRay(
			Translation,
			Position3.GlobalTransform.origin,
			new Godot.Collections.Array {
				this,
				ThirdOpenEnd,
				Game.PossessedPlayer.ValueOr(() => null)
			},
			2 | 4
		);
		if(Results.Count > 0 && Results["collider"] is OpenEnd) {
			ThirdEndMesh.Show();
			ThirdEndCollision.Disabled = false;
			System.Consume(((OpenEnd)Results["collider"]).Parent.System);
			Friends.Add(((OpenEnd)Results["collider"]).Parent);
		}
		else {
			ThirdEndMesh.Hide();
			ThirdEndCollision.Disabled = true;
		}

		Results = State.IntersectRay(
			Translation,
			Position4.GlobalTransform.origin,
			new Godot.Collections.Array {
				this,
				ForthOpenEnd,
				Game.PossessedPlayer.ValueOr(() => null)
			},
			2 | 4
		);
		if(Results.Count > 0 && Results["collider"] is OpenEnd) {
			ForthEndMesh.Show();
			ForthEndCollision.Disabled = false;
			System.Consume(((OpenEnd)Results["collider"]).Parent.System);
			Friends.Add(((OpenEnd)Results["collider"]).Parent);
		}
		else {
			ForthEndMesh.Hide();
			ForthEndCollision.Disabled = true;
		}

		Results = State.IntersectRay(
			Translation,
			Position5.GlobalTransform.origin,
			new Godot.Collections.Array {
				this,
				FifthOpenEnd,
				Game.PossessedPlayer.ValueOr(() => null)
			},
			2 | 4
		);
		if(Results.Count > 0 && Results["collider"] is OpenEnd) {
			FifthEndMesh.Show();
			FifthEndCollision.Disabled = false;
			System.Consume(((OpenEnd)Results["collider"]).Parent.System);
			Friends.Add(((OpenEnd)Results["collider"]).Parent);
		}
		else {
			FifthEndMesh.Hide();
			FifthEndCollision.Disabled = true;
		}

		Results = State.IntersectRay(
			Translation,
			Position6.GlobalTransform.origin,
			new Godot.Collections.Array {
				this,
				SixthOpenEnd,
				Game.PossessedPlayer.ValueOr(() => null)
			},
			2 | 4
		);
		if(Results.Count > 0 && Results["collider"] is OpenEnd) {
			SixthEndMesh.Show();
			SixthEndCollision.Disabled = false;
			System.Consume(((OpenEnd)Results["collider"]).Parent.System);
			Friends.Add(((OpenEnd)Results["collider"]).Parent);
		}
		else {
			SixthEndMesh.Hide();
			SixthEndCollision.Disabled = true;
		}

		if(InitiallyFilledFriends && !Friends.SetEquals(OriginalFriends)) {
			System = new PipeSystem(this);
			RecursiveAddFriendsToSystem();
		}
		InitiallyFilledFriends = true;
	}
}
