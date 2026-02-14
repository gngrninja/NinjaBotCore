pipeline {
  agent any

  options {
    timestamps()
    disableConcurrentBuilds()
  }

  environment {
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    NUGET_PACKAGES = "${WORKSPACE}/.nuget/packages"
  }

  stages {
    stage('Checkout') {
      steps {
        checkout scm
      }
    }

    stage('Restore') {
      steps {
        sh 'dotnet restore'
      }
    }

    stage('Build') {
      steps {
        sh 'dotnet build -c Release --no-restore'
      }
    }

    stage('Test') {
      steps {
        sh '''
          dotnet test -c Release --no-build \
            --logger "trx;LogFileName=test_results.trx" \
            --results-directory "${WORKSPACE}/TestResults"
        '''
      }
      post {
        always {
          archiveArtifacts artifacts: 'TestResults/**/*', allowEmptyArchive: true
        }
      }
    }

    stage('Staging') {
      when {
        branch 'Dev'
      }
      steps {
        sh 'sudo -n -u ninja ssh gnja@rpi /home/gnja/scripts/ninjabot/bot.sh'
      }
    }

    stage('Deploy') {
      when {
        // Require this to be a tag build and match semantic version tags
        allOf {
          buildingTag()
          tag pattern: "v\\d+\\.\\d+\\.\\d+", comparator: "REGEXP"
        }
      }
      steps {
        script {
          // Multibranch sets BRANCH_NAME to the tag (e.g., v2.0.4); fall back to GIT_BRANCH for single-branch jobs
          env.TAG_NAME = env.BRANCH_NAME ?: env.GIT_BRANCH?.replace('refs/tags/', '')?.replaceAll('.*/tags/', '')
        }
        sh '''
          echo "Deploying NinjaBot version ${TAG_NAME}..."

          # Load deployment configuration from server (POSIX shell)
          if [ -f /var/lib/jenkins/ninjabot.env ]; then
            . /var/lib/jenkins/ninjabot.env
            export NINJABOT_DEPLOY_DIR NINJABOT_DEPLOY_USER NINJABOT_DEPLOY_HOST
          else
            echo "Warning: /var/lib/jenkins/ninjabot.env not found, using defaults"
          fi

          chmod +x ./deploy.sh
          ./deploy.sh
        '''
      }
    }
  }

  post {
    success {
      emailext(
        from: "${env.EMAIL_FROM}",
        to: "${env.EMAIL_TO}",
        subject: "✅ SUCCESS: ${env.JOB_NAME} #${env.BUILD_NUMBER}",
        body: """
        <p><b>Build succeeded</b> 🎉</p>
        <p><b>Job:</b> ${env.JOB_NAME}</p>
        <p><b>Build:</b> #${env.BUILD_NUMBER}</p>
        <p><a href="${env.BUILD_URL}">Open build</a></p>
        """,
        mimeType: 'text/html',
        attachLog: true
      )
    }
    failure {
      emailext(
        from: "${env.EMAIL_FROM}",
        to: "${env.EMAIL_TO}",
        subject: "❌ FAILURE: ${env.JOB_NAME} #${env.BUILD_NUMBER}",
        body: """
        <p><b>Build failed</b> 💥</p>
        <p><b>Job:</b> ${env.JOB_NAME}</p>
        <p><b>Build:</b> #${env.BUILD_NUMBER}</p>
        <p><a href="${env.BUILD_URL}">Open logs</a></p>
        """,
        mimeType: 'text/html',
        attachLog: true
      )
    }
    always {
      cleanWs(deleteDirs: true, notFailBuild: true)
    }
  }
}
