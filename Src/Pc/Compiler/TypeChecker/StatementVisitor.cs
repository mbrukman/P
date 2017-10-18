using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Antlr4.Runtime;
using Microsoft.Pc.Antlr;
using Microsoft.Pc.TypeChecker.AST;
using Microsoft.Pc.TypeChecker.AST.Declarations;
using Microsoft.Pc.TypeChecker.AST.Expressions;
using Microsoft.Pc.TypeChecker.AST.Statements;
using Microsoft.Pc.TypeChecker.AST.States;
using Microsoft.Pc.TypeChecker.Types;

namespace Microsoft.Pc.TypeChecker
{
    public class StatementVisitor : PParserBaseVisitor<IPStmt>
    {
        private readonly ITranslationErrorHandler handler;
        private readonly Machine machine;
        private readonly Scope table;

        public StatementVisitor(Scope table, Machine machine, ITranslationErrorHandler handler)
        {
            this.table = table;
            this.machine = machine;
            this.handler = handler;
        }

        public override IPStmt VisitCompoundStmt(PParser.CompoundStmtContext context)
        {
            var statements = context.statement().Select(Visit);
            return new CompoundStmt(statements.ToList());
        }

        public override IPStmt VisitPopStmt(PParser.PopStmtContext context) { return new PopStmt(); }

        public override IPStmt VisitAssertStmt(PParser.AssertStmtContext context)
        {
            var exprVisitor = new ExprVisitor(table, handler);
            IPExpr assertion = exprVisitor.Visit(context.expr());
            if (assertion.Type != PrimitiveType.Bool)
            {
                throw handler.TypeMismatch(context.expr(), assertion.Type, PrimitiveType.Bool);
            }
            string message = context.StringLiteral()?.GetText() ?? "";
            return new AssertStmt(assertion, message);
        }

        public override IPStmt VisitPrintStmt(PParser.PrintStmtContext context)
        {
            var exprVisitor = new ExprVisitor(table, handler);
            string message = context.StringLiteral().GetText();
            int numNecessaryArgs = (from Match match in Regex.Matches(message, @"(?:{{|}}|{(\d+)}|[^{}]+|{|})")
                                    where match.Groups[1].Success
                                    select int.Parse(match.Groups[1].Value) + 1)
                .Concat(new[] {0})
                .Max();
            var argsExprs = context.rvalueList()?.rvalue().Select(rvalue => exprVisitor.Visit(rvalue)) ??
                            Enumerable.Empty<IPExpr>();
            var args = argsExprs.ToList();
            if (args.Count < numNecessaryArgs)
            {
                throw handler.IncorrectArgumentCount(
                                                     (ParserRuleContext) context.rvalueList() ?? context,
                                                     args.Count,
                                                     numNecessaryArgs);
            }
            if (args.Count > numNecessaryArgs)
            {
                handler.IssueWarning((ParserRuleContext) context.rvalueList() ?? context,
                                     "ignoring extra arguments to print expression");
                args = args.Take(numNecessaryArgs).ToList();
            }
            return new PrintStmt(message, args);
        }

        public override IPStmt VisitReturnStmt(PParser.ReturnStmtContext context)
        {
            var exprVisitor = new ExprVisitor(table, handler);
            return new ReturnStmt(context.expr() == null ? null : exprVisitor.Visit(context.expr()));
        }

        public override IPStmt VisitAssignStmt(PParser.AssignStmtContext context)
        {
            var exprVisitor = new ExprVisitor(table, handler);
            IPExpr variable = exprVisitor.Visit(context.lvalue());
            IPExpr value = exprVisitor.Visit(context.rvalue());
            if (!(value is ILinearRef linearRef))
            {
                return new AssignStmt(variable, value);
            }
            if (!variable.Type.IsAssignableFrom(linearRef.Variable.Type))
            {
                throw handler.TypeMismatch(context.rvalue(), linearRef.Variable.Type, variable.Type);
            }
            switch (linearRef.LinearType)
            {
                case LinearType.Move:
                    return new MoveAssignStmt(variable, linearRef.Variable);
                case LinearType.Swap:
                    return new SwapAssignStmt(variable, linearRef.Variable);
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public override IPStmt VisitInsertStmt(PParser.InsertStmtContext context)
        {
            throw new NotImplementedException("insert statements");
        }

        public override IPStmt VisitRemoveStmt(PParser.RemoveStmtContext context)
        {
            throw new NotImplementedException("remove statements");
        }

        public override IPStmt VisitWhileStmt(PParser.WhileStmtContext context)
        {
            var exprVisitor = new ExprVisitor(table, handler);
            IPExpr condition = exprVisitor.Visit(context.expr());
            if (condition.Type != PrimitiveType.Bool)
            {
                throw handler.TypeMismatch(context.expr(), condition.Type, PrimitiveType.Bool);
            }
            IPStmt body = Visit(context.statement());
            return new WhileStmt(condition, body);
        }

        public override IPStmt VisitIfStmt(PParser.IfStmtContext context)
        {
            var exprVisitor = new ExprVisitor(table, handler);
            IPExpr condition = exprVisitor.Visit(context.expr());
            if (condition.Type != PrimitiveType.Bool)
            {
                throw handler.TypeMismatch(context.expr(), condition.Type, PrimitiveType.Bool);
            }
            IPStmt thenBody = Visit(context.thenBranch);
            IPStmt elseBody = context.elseBranch == null ? null : Visit(context.elseBranch);
            return new IfStmt(condition, thenBody, elseBody);
        }

        public override IPStmt VisitCtorStmt(PParser.CtorStmtContext context)
        {
            var exprVisitor = new ExprVisitor(table, handler);
            string machineName = context.iden().GetText();
            if (table.Lookup(machineName, out Machine machine))
            {
                bool hasArguments = machine.PayloadType != PrimitiveType.Null;
                var args = context.rvalueList()?.rvalue().Select(rv => exprVisitor.Visit(rv)) ??
                           Enumerable.Empty<IPExpr>();
                if (hasArguments)
                {
                    var argsList = args.ToList();
                    if (argsList.Count != 1)
                    {
                        throw handler.IncorrectArgumentCount((ParserRuleContext) context.rvalueList() ?? context,
                                                             argsList.Count,
                                                             1);
                    }
                    return new CtorStmt(machine, argsList);
                }
                if (args.Count() != 0)
                {
                    handler.IssueWarning((ParserRuleContext) context.rvalueList() ?? context,
                                         "ignoring extra parameters passed to machine constructor");
                }
                return new CtorStmt(machine, new List<IPExpr>());
            }
            throw handler.MissingDeclaration(context.iden(), "machine", machineName);
        }

        public override IPStmt VisitFunCallStmt(PParser.FunCallStmtContext context)
        {
            var exprVisitor = new ExprVisitor(table, handler);
            string funName = context.fun.GetText();
            var args = context.rvalueList()?.rvalue().Select(rv => exprVisitor.Visit(rv)) ?? Enumerable.Empty<IPExpr>();
            var argsList = args.ToList();
            if (table.Lookup(funName, out Function fun))
            {
                if (fun.Signature.Parameters.Count != argsList.Count)
                {
                    throw handler.IncorrectArgumentCount((ParserRuleContext) context.rvalueList() ?? context,
                                                         argsList.Count,
                                                         fun.Signature.Parameters.Count);
                }
                foreach (var pair in fun.Signature.Parameters.Zip(argsList, Tuple.Create))
                {
                    ITypedName param = pair.Item1;
                    IPExpr arg = pair.Item2;
                    if (!param.Type.IsAssignableFrom(arg.Type))
                    {
                        throw handler.TypeMismatch(context, arg.Type, param.Type);
                    }
                }
                return new FunCallStmt(fun, argsList);
            }
            if (table.Lookup(funName, out FunctionProto proto))
            {
                throw new NotImplementedException("function prototype call statement");
            }
            throw handler.MissingDeclaration(context.fun, "function or function prototype", funName);
        }

        public override IPStmt VisitRaiseStmt(PParser.RaiseStmtContext context)
        {
            var exprVisitor = new ExprVisitor(table, handler);
            IPExpr pExpr = exprVisitor.Visit(context.expr());
            if (!(pExpr is EventRefExpr eventRef))
            {
                throw new NotImplementedException("raising dynamic events");
            }

            var args = (context.rvalueList()?.rvalue().Select(rv => exprVisitor.Visit(rv)) ??
                        Enumerable.Empty<IPExpr>()).ToList();
            PEvent evt = eventRef.PEvent;
            if (evt.Name.Equals("null"))
            {
                throw handler.EmittedNullEvent(context.expr());
            }

            if (evt.PayloadType == PrimitiveType.Null && args.Count == 0 ||
                evt.PayloadType != PrimitiveType.Null && args.Count == 1)
            {
                return new RaiseStmt(eventRef.PEvent, args.Count == 0 ? null : args[0]);
            }
            throw handler.IncorrectArgumentCount((ParserRuleContext) context.rvalueList() ?? context,
                                                 args.Count,
                                                 evt.PayloadType == PrimitiveType.Null ? 0 : 1);
        }

        public override IPStmt VisitSendStmt(PParser.SendStmtContext context)
        {
            var exprVisitor = new ExprVisitor(table, handler);
            IPExpr machineExpr = exprVisitor.Visit(context.machine);
            if (machineExpr.Type != PrimitiveType.Machine)
            {
                throw handler.TypeMismatch(context.machine, machineExpr.Type, PrimitiveType.Machine);
            }
            IPExpr evtExpr = exprVisitor.Visit(context.@event);
            if (!(evtExpr is EventRefExpr evtRef))
            {
                throw new NotImplementedException("sending dynamic events");
            }

            PEvent evt = evtRef.PEvent;
            if (evt.Name.Equals("null"))
            {
                throw handler.EmittedNullEvent(context.@event);
            }

            var args = context.rvalueList()?.rvalue().Select(rv => exprVisitor.Visit(rv)) ?? Enumerable.Empty<IPExpr>();
            var argsList = args.ToList();
            if (evt.PayloadType != PrimitiveType.Null && argsList.Count == 1)
            {
                if (!evt.PayloadType.IsAssignableFrom(argsList[0].Type))
                {
                    throw handler.TypeMismatch(context.rvalueList().rvalue(0), argsList[0].Type, evt.PayloadType);
                }
                return new SendStmt(machineExpr, evt, argsList);
            }
            if (evt.PayloadType == PrimitiveType.Null && argsList.Count == 0)
            {
                return new SendStmt(machineExpr, evt, argsList);
            }
            throw handler.IncorrectArgumentCount((ParserRuleContext) context.rvalueList() ?? context,
                                                 argsList.Count,
                                                 evt.PayloadType == PrimitiveType.Null ? 0 : 1);
        }

        public override IPStmt VisitAnnounceStmt(PParser.AnnounceStmtContext context)
        {
            var exprVisitor = new ExprVisitor(table, handler);
            IPExpr pExpr = exprVisitor.Visit(context.expr());
            if (!(pExpr is EventRefExpr eventRef))
            {
                throw new NotImplementedException("announcing dynamic events");
            }

            var args = (context.rvalueList()?.rvalue().Select(rv => exprVisitor.Visit(rv)) ??
                        Enumerable.Empty<IPExpr>()).ToList();
            PEvent evt = eventRef.PEvent;
            if (evt.PayloadType == PrimitiveType.Null && args.Count == 0 ||
                evt.PayloadType != PrimitiveType.Null && args.Count == 1)
            {
                return new AnnounceStmt(eventRef.PEvent, args.Count == 0 ? null : args[0]);
            }
            throw handler.IncorrectArgumentCount((ParserRuleContext) context.rvalueList() ?? context,
                                                 args.Count,
                                                 evt.PayloadType == PrimitiveType.Null ? 0 : 1);
        }

        public override IPStmt VisitGotoStmt(PParser.GotoStmtContext context)
        {
            PParser.StateNameContext stateNameContext = context.stateName();
            string stateName = stateNameContext.state.GetText();
            IStateContainer current = machine;
            foreach (PParser.IdenContext token in stateNameContext._groups)
            {
                current = current?.GetGroup(token.GetText());
                if (current == null)
                {
                    throw handler.MissingDeclaration(token, "group", token.GetText());
                }
            }
            State state = current?.GetState(stateName);
            if (state == null)
            {
                throw handler.MissingDeclaration(stateNameContext.state, "state", stateName);
            }
            IPExpr payload = null;
            if (context.rvalueList() != null)
            {
                throw new NotImplementedException("goto statement with payload");
            }

            return new GotoStmt(state, payload);
        }

        public override IPStmt VisitReceiveStmt(PParser.ReceiveStmtContext context)
        {
            throw new NotImplementedException("receive statements");
        }

        public override IPStmt VisitNoStmt(PParser.NoStmtContext context) { return new NoStmt(); }
    }
}