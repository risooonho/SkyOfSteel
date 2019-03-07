using Godot;
using System;
using System.Collections.Generic;
using IronPython;
using IronPython.Hosting;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;


public class Scripting : Node
{
	public static ScriptEngine ConsoleEngine;
	public static ScriptScope ConsoleScope;
	public static ScriptScope StringScope; //Used to convert objects to string
	public static ScriptEngine GmEngine;
	public static ScriptScope GmScope;

	public static string GamemodeName;
	public static string ClientGmScript;

	public static Scripting Self;
	Scripting()
	{
		if(Engine.EditorHint) {return;}

		Self = this;

		ConsoleEngine = Python.CreateEngine(new Dictionary<string,object>() { {"DivisionOptions", PythonDivisionOptions.New} });
		ConsoleScope = ConsoleEngine.CreateScope();
		StringScope = ConsoleEngine.CreateScope();

		foreach(List<object> List in API.Expose(API.LEVEL.CONSOLE, this))
		{
			ConsoleScope.SetVariable((string)List[0], (Delegate)List[1]);
		}
		foreach(API.PyConstructorExposer Exposer in API.ExposeConstructors(API.LEVEL.CONSOLE))
		{
			ConsoleScope.SetVariable(Exposer.Name, Exposer.Constructor);
		}

		SetupGmEngine();
	}


	public static object ToPy(object ToConvert)
	{
		if(ToConvert is Vector3)
		{
			//Could use two layers of casting to implicitly convert
			//This is just nicer to read
			return new PyVector3((Vector3)ToConvert);
		}

		//Does not require intervention
		return ToConvert;
	}


	public static object[] ToPy(params object[] ConvertArray)
	{
		object[] Out = new object[ConvertArray.Length];
		int Iteration = 0;
		foreach(object ToConvert in ConvertArray)
		{
			Out[Iteration] = ToPy(ToConvert);
			Iteration += 1;
		}
		return Out;
	}

	public static void SetupGmEngine()
	{
		GmEngine = Python.CreateEngine(new Dictionary<string,object>() { {"DivisionOptions", PythonDivisionOptions.New} });
		GmScope = ConsoleEngine.CreateScope();

		foreach(List<object> List in API.Expose(API.LEVEL.SERVER_GM, Self))
		{
			GmScope.SetVariable((string)List[0], (Delegate)List[1]);
		}
		foreach(API.PyConstructorExposer Exposer in API.ExposeConstructors(API.LEVEL.CONSOLE))
		{
			GmScope.SetVariable(Exposer.Name, Exposer.Constructor);
		}
	}


	public static void SetupClientEngine()
	{
		/*ClientGmEngine = new Jurassic.ScriptEngine();
		foreach(List<object> List in API.Expose(API.LEVEL.CLIENT_GM, Self))
		{
			ClientGmEngine.SetGlobalFunction((string)List[0], (Delegate)List[1]);
		}
		foreach(List<object> List in API.ExposeConstructors(API.LEVEL.CLIENT_GM))
		{
			ClientGmEngine.SetGlobalValue((string)List[0], (ClrFunction)List[1]);
		}*/
	}


	public override void _Ready()
	{
		File SetupScript = new File();
		SetupScript.Open("res://Scripting/SetupScript.py", 1);
		try
		{
			ScriptSource Source = ConsoleEngine.CreateScriptSourceFromString(SetupScript.GetAsText(), SourceCodeKind.Statements);
			Source.Execute(ConsoleScope);
		}
		catch(Exception e)
		{
			ExceptionOperations eo = ConsoleEngine.GetService<ExceptionOperations>();
			Console.Print(eo.FormatException(e));
		}
		SetupScript.Close();
	}


	public override void _PhysicsProcess(float Delta)
	{
		/*try
		{
			ConsoleEngine.CallGlobalFunction("_tick", new object[] {(double)Delta});
		}
		catch(System.InvalidOperationException){} //This just means that _tick is not a delcared function
		catch(JavaScriptException Error)
		{
			Console.Print(Error.Message);
		}


		if(GetTree().GetNetworkPeer() != null)
		{
			try
			{
				ClientGmEngine.CallGlobalFunction("_tick", new object[] {(double)Delta});
			}
			catch(System.InvalidOperationException){} //This just means that _tick is not a delcared function
			catch(JavaScriptException){}

			if(GetTree().IsNetworkServer())
			{
				try
				{
					ServerGmEngine.CallGlobalFunction("_tick", new object[] {(double)Delta});
				}
				catch(System.InvalidOperationException){} //This just means that _tick is not a delcared function
				catch(JavaScriptException){}
			}
		}*/
	}


	public static void LoadGameMode(string Name)
	{
		UnloadGameMode();

		Directory ModeDir = new Directory();
		if(ModeDir.FileExists($"user://gamemodes/{Name}/{Name}.py")) //Has a  script
		{
			Console.Log($"Loaded gamemode '{Name}', executing");

			GamemodeName = Name;
			SetupGmEngine();
			File ServerScript = new File();
			ServerScript.Open($"user://gamemodes/{Name}/{Name}.py", 1);

			try
			{
				GmEngine.Execute(ServerScript.GetAsText(), GmScope);
			}
			catch(Exception Err)
			{
				ExceptionOperations EO = GmEngine.GetService<ExceptionOperations>();
				Console.Print(EO.FormatException(Err));
				Scripting.UnloadGameMode();
			}

			ServerScript.Close();
		}
		else
		{
			Console.ThrowPrint($"No gamemode named '{Name}'");
		}
	}


	public static void UnloadGameMode()
	{
		if(GamemodeName != null)
		{
			Console.Log($"The gamemode '{GamemodeName}' was unloaded");
			GamemodeName = null;
			SetupGmEngine();
		}
	}


	[Remote]
	public void NetLoadClientScript(string Script)
	{
		/*Console.Log("Recieved client.js from server, executing");
		SetupClientEngine();
		ClientGmEngine.Execute(Script);*/
	}


	public static string PyToString(object Obj)
	{
		StringScope.SetVariable("to_string_object", Obj);
		return ConsoleEngine.Execute("str(to_string_object)", StringScope);
	}


	public static void RunConsoleLine(string Line)
	{
		try
		{
			object Returned = ConsoleEngine.Execute(Line, ConsoleScope);
			if(Returned != null)
			{
				Console.Print(PyToString(Returned));
			}
		}
		catch(Exception e)
		{
			ExceptionOperations eo = ConsoleEngine.GetService<ExceptionOperations>();
			Console.Print(eo.FormatException(e));
		}
	}
}
