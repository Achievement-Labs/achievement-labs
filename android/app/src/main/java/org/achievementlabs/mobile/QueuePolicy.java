package org.achievementlabs.mobile;
final class QueuePolicy {
 static boolean review(String s){return s.startsWith("Failed")||s.startsWith("Interrupted")||s.startsWith("Unconfirmed")||s.startsWith("Needs review");}
 static boolean terminal(String s){return s.startsWith("Accepted")||s.startsWith("Already unlocked")||s.startsWith("Confirmed")||s.equals("Skipped by user");}
 static boolean transientFailure(Exception e){return e instanceof java.io.IOException || e instanceof Net.HttpFailure&&(((Net.HttpFailure)e).status>=500||((Net.HttpFailure)e).status==408);}
}
