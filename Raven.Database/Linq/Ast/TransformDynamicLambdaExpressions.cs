﻿using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.NRefactory.CSharp;
using ICSharpCode.NRefactory.PatternMatching;

namespace Raven.Database.Linq.Ast
{
	public class TransformDynamicLambdaExpressions : DepthFirstAstVisitor<object,object>
	{
		public override object VisitLambdaExpression(LambdaExpression lambdaExpression, object data)
		{
			var invocationExpression = lambdaExpression.Parent as InvocationExpression;
			if (invocationExpression == null)
				return base.VisitLambdaExpression(lambdaExpression, data);

			var target = invocationExpression.Target as MemberReferenceExpression;
			if(target == null)
				return base.VisitLambdaExpression(lambdaExpression, data);

			AstNode node = lambdaExpression;
			var parenthesizedlambdaExpression = new ParenthesizedExpression(lambdaExpression);
			switch (target.MemberName)
			{
				case "Sum":
				case "Average":
					node = ModifyLambdaForNumerics(lambdaExpression, parenthesizedlambdaExpression);
					break;
				case "Max":
				case "Min":
					node = ModifyLambdaForMinMax(lambdaExpression, parenthesizedlambdaExpression);
					break;
				case "OrderBy":
				case "OrderByDescending":
				case "GroupBy":
				case "Recurse":
				case "Select":
					node = ModifyLambdaForSelect(parenthesizedlambdaExpression, target);
					break;
				case "SelectMany":
					node = ModifyLambdaForSelectMany(lambdaExpression, parenthesizedlambdaExpression, invocationExpression);
					break;
				case "Any":
				case "all":
				case "First":
				case "FirstOrDefault":
				case "Last":
				case "LastOfDefault":
				case "Single":
				case "Where":
				case "Count":
				case "SingleOrDefault":
					node = new CastExpression(new SimpleType("Func<dynamic, bool>"), parenthesizedlambdaExpression);
				break;
			}
			lambdaExpression.ReplaceWith(node);

			return base.VisitLambdaExpression(lambdaExpression, data);
		}

		private static AstNode ModifyLambdaForSelect(ParenthesizedExpression parenthesizedlambdaExpression,
		                                           MemberReferenceExpression target)
		{
			var parentInvocation = target.Target as InvocationExpression;
			if(parentInvocation != null)
			{
				var parentTarget = parentInvocation.Target as MemberReferenceExpression;
				if(parentTarget != null && parentTarget.MemberName == "GroupBy")
				{
					return new CastExpression(new SimpleType("Func<IGrouping<dynamic,dynamic>, dynamic>"), parenthesizedlambdaExpression);
				}
			}
			return new CastExpression(AstType.Create(typeof(Func<dynamic, dynamic>)), parenthesizedlambdaExpression);
		}

		private static AstNode ModifyLambdaForSelectMany(LambdaExpression lambdaExpression,
		                                               ParenthesizedExpression parenthesizedlambdaExpression,
		                                               InvocationExpression invocationExpression)
		{
			AstNode node = lambdaExpression;
			if(invocationExpression.Arguments.Count > 0 && invocationExpression.Arguments.ElementAt(0) == lambdaExpression)// first one, select the collection
			{
				// need to enter a cast for (IEnumerable<dynamic>) on the end of the lambda body
				var selectManyExpression = new LambdaExpression
				{
					Body =
						new CastExpression(AstType.Create(typeof(IEnumerable<dynamic>)),
						                   new ParenthesizedExpression((Expression)lambdaExpression.Body)),
				};
				selectManyExpression.Parameters.AddRange(lambdaExpression.Parameters);

				node = new CastExpression(AstType.Create(typeof(Func<dynamic, IEnumerable<dynamic>>)),
				                          new ParenthesizedExpression(selectManyExpression));
			}
			else if (invocationExpression.Arguments.Count > 1 && invocationExpression.Arguments.ElementAt(1) == lambdaExpression)// first one, select the collection
			{
				node = new CastExpression(AstType.Create(typeof (Func<dynamic, dynamic, dynamic>)), parenthesizedlambdaExpression);
			}
			return node;
		}

		private static AstNode ModifyLambdaForMinMax(LambdaExpression lambdaExpression,
		                                           ParenthesizedExpression parenthesizedlambdaExpression)
		{
			var node = new CastExpression(AstType.Create(typeof(Func<dynamic, IComparable>)), parenthesizedlambdaExpression);
			var castExpression = GetAsCastExpression(lambdaExpression.Body);
			if (castExpression != null)
			{
				var castToType = new SimpleType("Func", new SimpleType("dynamic"), castExpression.Type);
				node = new CastExpression(castToType, parenthesizedlambdaExpression);
			}
			return node;
		}

		private static CastExpression GetAsCastExpression(INode expressionBody)
		{
			var castExpression = expressionBody as CastExpression;
			if (castExpression != null)
				return castExpression;
			var parametrizedNode = expressionBody as ParenthesizedExpression;
			if (parametrizedNode != null)
				return GetAsCastExpression(parametrizedNode.Expression);
			return null;
		}

		private static AstNode ModifyLambdaForNumerics(LambdaExpression lambdaExpression,
		                                        ParenthesizedExpression parenthesizedlambdaExpression)
		{
			var castExpression = GetAsCastExpression(lambdaExpression.Body);
			if (castExpression != null)
			{
				var castToType = new SimpleType("Func", new SimpleType("dynamic"), castExpression.Type);
				return new CastExpression(castToType, parenthesizedlambdaExpression);
			}
			var expression = new LambdaExpression
			{
				Body = new CastExpression(AstType.Create(typeof(decimal)), new ParenthesizedExpression((Expression)lambdaExpression.Body)),
			};
			expression.Parameters.AddRange(lambdaExpression.Parameters);

			return new CastExpression(AstType.Create(typeof(Func<dynamic, decimal>)),
			                          new ParenthesizedExpression(expression));

		}
	}
}