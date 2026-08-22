using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using System.Collections.Generic;

namespace MinorShift.Emuera.Runtime.Script.Statements.Function;

internal sealed class FunctionMethodTerm : AExpression
{
	public FunctionMethodTerm(FunctionMethod meth, List<AExpression> args)
		: base(meth.ReturnType)
	{
		method = meth;
		arguments = args;
	}

	private FunctionMethod method;
	private List<AExpression> arguments;

	public override long GetIntValue(ExpressionMediator exm)
	{
		return method.GetIntValue(exm, arguments);
	}
	public override string GetStrValue(ExpressionMediator exm)
	{
		return method.GetStrValue(exm, arguments);
	}
	public override SingleTerm GetValue(ExpressionMediator exm)
	{
		return method.GetReturnValue(exm, arguments);
	}

	// CloudEmuera: allow the caller to recognize a direct ESCAPE expression
	// before it is converted to a compiled Regex instance.
	internal bool TryGetEscapedLiteral(ExpressionMediator exm, out string literal)
	{
		return method.TryGetEscapedLiteral(exm, arguments, out literal);
	}

	public override AExpression Restructure(ExpressionMediator exm)
	{
		if (method.HasUniqueRestructure)
		{
			if (method.UniqueRestructure(exm, [.. arguments]) && method.CanRestructure)
				return GetValue(exm);
			return this;
		}
		bool argIsConst = true;
		for (int i = 0; i < arguments.Count; i++)
		{
			if (arguments[i] == null)
				continue;
			arguments[i] = arguments[i].Restructure(exm);
			argIsConst &= arguments[i] is SingleTerm;
		}
		if (method.CanRestructure && argIsConst)
			return GetValue(exm);
		return this;

	}

}
